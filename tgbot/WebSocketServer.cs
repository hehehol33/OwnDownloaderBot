using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TikTok_bot
{
    /// <summary>
    /// Класс, предназначенный для обработки WebSocket соединений и отправки данных клиентам.
    /// </summary>
    internal class WebSocketServer
    {
        const string settingsFilePath = "settings.json";

        /// <summary>
        /// Получает полное сообщение от клиента через WebSocket, учитывая возможный недостаточный размер буфера.
        /// </summary>
        /// <param name="ws">WebSocket соединение клиента.</param>
        /// <param name="buffer">Буфер, в который будут считываться данные с клиента.</param>
        /// <returns>Полное сообщение от клиента в виде строки.</returns>
        static async Task<string> ReceiveFullMessage(WebSocket ws, byte[] buffer)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - WebSocket client disconnected");
                    break;
                }

                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            string message = Encoding.UTF8.GetString(ms.ToArray());
            return message;
        }

        /// <summary>
        /// Проверяет, активен ли подпись для указанного пользователя.
        /// </summary>
        /// <param name="chatId">ID чата пользователя.</param>
        /// <returns>True, если подпись активна, иначе false.</returns>
        private static bool IsSignatureActive(long chatId)
        {
            var userStorage = new UserStorage(settingsFilePath);
            var users = userStorage.LoadUsers();
            var user = users.FirstOrDefault(u => u.Id == chatId);

            // Если пользователь не найден, возвращаем true (по умолчанию подпись включена)
            return user == null || user.IsActive;
        }

        /// <summary>
        /// Обрабатывает подключение WebSocket клиента, получает и отправляет данные через WebSocket.
        /// </summary>
        /// <param name="ws">WebSocket соединение клиента.</param>
        /// <param name="clients">Список всех подключенных WebSocket клиентов.</param>
        /// <param name="clientPlatforms">Словарь, связывающий WebSocket с платформой клиента.</param>
        /// <param name="clientChatIds">Словарь, связывающий WebSocket с идентификатором чата клиента.</param>
        /// <param name="clientUpdates">Словарь, связывающий WebSocket с обновлениями от клиента.</param>
        /// <param name="bot">Объект Telegram бота для отправки сообщений и медиа.</param>
        /// <param name="isUsingLocalTGServer">Флаг, указывающий, используется ли локальный сервер Telegram.</param>
        /// <returns>Задача, представляющая асинхронную операцию обработки соединения.</returns>
        public static async Task HandleWebSocket(
            WebSocket ws,
            List<WebSocket> clients,
            Dictionary<WebSocket, string> clientPlatforms,
            Dictionary<WebSocket, long> clientChatIds,
            Dictionary<WebSocket, Update?> clientUpdates,
            ITelegramBotClient bot,
            bool isUsingLocalTGServer = false)  // Add parameter to check if using local server
        {
            byte[] buffer = new byte[2048];

            // Получаем данные о регистрации
            var registrationData = await ReceiveFullMessage(ws, buffer);
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Received registration: " + registrationData);

            if (registrationData.StartsWith("platform:"))
            {
                string platform = registrationData.Split(':')[1].Trim().ToLower();
                clientPlatforms[ws] = platform;
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Client registered for platform: " + platform);
            }
            else
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Invalid registration message.");
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid registration", CancellationToken.None);
                return;
            }
            clients.Add(ws); // Добавляем клиента в список

            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var receivedData = await ReceiveFullMessage(ws, buffer);
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Received data: " + receivedData);

                    if (clientChatIds.TryGetValue(ws, out long chatId))
                    {
                        try
                        {
                            if (clientUpdates.TryGetValue(ws, out Update? update) && update != null)
                            {
                                string sender = update.Message?.From?.Username ?? update.Message?.From?.FirstName ?? "Пользователь";
                                var jsonData = JsonSerializer.Deserialize<JsonElement>(receivedData);

                                // Проверяем, активна ли подпись для этого пользователя
                                bool signatureActive = IsSignatureActive(chatId);

                                if (jsonData.ValueKind == JsonValueKind.Object)
                                {
                                    if (jsonData.TryGetProperty("media", out JsonElement mediaElement) && mediaElement.ValueKind == JsonValueKind.Array)
                                    {
                                        var mediaList = new List<IAlbumInputMedia>();
                                        string textContent = null;
                                        bool hasFileProtocolUrl = false;
                                        string? fileToDelete = null;

                                        foreach (var item in mediaElement.EnumerateArray())
                                        {
                                            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out JsonElement typeElement))
                                            {
                                                string type = typeElement.GetString();

                                                // Check for file:// URLs
                                                if ((type == "photo" || type == "video") &&
                                                    item.TryGetProperty("url", out JsonElement urlElement))
                                                {
                                                    string url = urlElement.GetString() ?? "";


                                                    if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        if (!isUsingLocalTGServer)
                                                        {
                                                            hasFileProtocolUrl = true;
                                                            continue;
                                                        }

                                                        string localPath = url.Replace("file://", "").Replace('/', Path.DirectorySeparatorChar);

                                                        if (File.Exists(localPath))
                                                        {
                                                            fileToDelete = localPath;
                                                            var stream = File.OpenRead(localPath);
                                                            if (type == "photo")
                                                                mediaList.Add(new InputMediaPhoto(InputFile.FromStream(stream, Path.GetFileName(localPath))));
                                                            else if (type == "video")
                                                                mediaList.Add(new InputMediaVideo(InputFile.FromStream(stream, Path.GetFileName(localPath))));


                                                        }
                                                        else
                                                        {
                                                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - File not found: {localPath}");
                                                            await bot.SendMessage(chatId, $"Error: File not found: {Path.GetFileName(localPath)}");
                                                        }

                                                        continue;
                                                    }

                                                    if (type == "photo" && !string.IsNullOrEmpty(url))
                                                    {
                                                        mediaList.Add(new InputMediaPhoto(InputFile.FromUri(url)));
                                                    }
                                                    else if (type == "video" && !string.IsNullOrEmpty(url))
                                                    {
                                                        mediaList.Add(new InputMediaVideo(InputFile.FromUri(url)));
                                                    }

                                                }
                                                else if (type == "text" && item.TryGetProperty("content", out JsonElement contentElement))
                                                {
                                                    textContent = contentElement.GetString();
                                                }
                                            }
                                        }
                                        // Rest of the media handling code remains the same
                                        ChatAction action = mediaList.Count > 0
                                            ? (mediaList[0] is InputMediaVideo ? ChatAction.UploadVideo : ChatAction.UploadPhoto)
                                            : ChatAction.Typing;

                                        await bot.SendChatAction(chatId, action);

                                        // If we found file:// URLs but we're not using local server, send an error
                                        if (hasFileProtocolUrl && !isUsingLocalTGServer)
                                        {
                                            await bot.SendMessage(chatId, "Error: Downloading this content is not possible without a local Telegram server.");
                                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Blocked file:// URL for user: {sender} - using official API");
                                            continue;
                                        }


                                        if (mediaList.Count > 0)
                                        {
                                            // Add caption to first media if there's text
                                            if (!string.IsNullOrEmpty(textContent))
                                            {
                                                string caption = textContent;
                                                if (signatureActive)
                                                {
                                                    caption += $"\n\nSent by {sender}";
                                                }

                                                if (mediaList[0] is InputMediaPhoto photoMedia)
                                                {
                                                    photoMedia.Caption = caption;
                                                }
                                                else if (mediaList[0] is InputMediaVideo videoMedia)
                                                {
                                                    videoMedia.Caption = caption;
                                                }
                                            }
                                            else if (signatureActive)
                                            {
                                                // Add simple sender attribution only if signature is active
                                                if (mediaList[0] is InputMediaPhoto photoMedia)
                                                {
                                                    photoMedia.Caption = $"Sent by {sender}";
                                                }
                                                else if (mediaList[0] is InputMediaVideo videoMedia)
                                                {
                                                    videoMedia.Caption = $"Sent by {sender}";
                                                }
                                            }


                                            await bot.SendMediaGroup(chatId, mediaList);
                                            if (fileToDelete != null)
                                            {
                                                try
                                                {
                                                    File.Delete(fileToDelete);
                                                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Deleted file: {fileToDelete}");
                                                }
                                                catch (Exception ex)
                                                {
                                                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Failed to delete {fileToDelete}: {ex.Message}");
                                                }
                                            }


                                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Sent media group to user: {sender}");
                                        }
                                        else if (!string.IsNullOrEmpty(textContent))
                                        {
                                            string message = textContent;
                                            if (signatureActive)
                                            {
                                                message += $"\n\nSent by {sender}";
                                            }
                                            await bot.SendMessage(chatId, message);
                                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Sent text message to user: {sender}");
                                        }
                                    }
                                    else if (jsonData.TryGetProperty("error", out JsonElement errorElement))
                                    {
                                        string? errorMessage = errorElement.GetString();
                                        await bot.SendMessage(chatId, $"{errorMessage}");
                                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Sent error message to user: {sender}");
                                    }
                                    else
                                    {
                                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Error: Unsupported data type.");
                                    }
                                }
                                else
                                {
                                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Error: Unsupported data type.");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - JSON processing error: " + ex.Message);
                        }
                    }
                }
            }
            finally
            {
                clients.Remove(ws);
                clientPlatforms.Remove(ws);
                clientChatIds.Remove(ws);
                clientUpdates.Remove(ws);
            }
        }
    }
}
