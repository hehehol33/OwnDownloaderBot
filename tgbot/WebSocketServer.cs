using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Linq;
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

        // Telegram limits
        private const int MaxMediaPerGroup = 10;
        private const int MaxCaptionLength = 1024;    // for media messages
        private const int MaxTextLength = 4096;       // for text-only messages

        /// <summary>
        /// Split text into chunks with a maximum length. Tries to split on whitespace when possible.
        /// </summary>
        private static IEnumerable<string> SplitText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) yield break;

            int idx = 0;
            while (idx < text.Length)
            {
                int len = Math.Min(maxLength, text.Length - idx);
                // Try to break on the last whitespace within the window (only when not at the end)
                int breakPos = -1;
                if (idx + len < text.Length)
                {
                    breakPos = text.LastIndexOfAny(new[] { ' ', '\n', '\t' }, idx + len - 1, len);
                }

                if (breakPos > idx)
                {
                    yield return text.Substring(idx, breakPos - idx + 1).TrimEnd();
                    idx = breakPos + 1;
                }
                else
                {
                    yield return text.Substring(idx, len);
                    idx += len;
                }
            }
        }

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
                    Logger.Info("WebSocket client disconnected");
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
            Logger.Debug("Received registration: " + registrationData);

            if (registrationData.StartsWith("platform:"))
            {
                string platform = registrationData.Split(':')[1].Trim().ToLower();
                clientPlatforms[ws] = platform;
                Logger.Info("Client registered for platform: " + platform);
            }
            else
            {
                Logger.Warning("Invalid registration message.");
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid registration", CancellationToken.None);
                return;
            }
            clients.Add(ws); // Добавляем клиента в список

            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var receivedData = await ReceiveFullMessage(ws, buffer);
                    Logger.Debug("Received data: " + receivedData);

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
                                        string? textContent = null;
                                        bool hasFileProtocolUrl = false;
                                        var filesToDelete = new List<string>();

                                        foreach (var item in mediaElement.EnumerateArray())
                                        {
                                            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out JsonElement typeElement))
                                            {
                                                string type = typeElement.GetString() ?? string.Empty;

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

                                                        string localPath = FileIO.FileUrlToLocalPath(url);

                                                        if (FileIO.FileExists(localPath))
                                                        {
                                                            filesToDelete.Add(localPath);
                                                            var stream = FileIO.OpenFileForReading(localPath);
                                                            string fileName = FileIO.GetFileName(localPath);
                                                            
                                                            if (type == "photo")
                                                                mediaList.Add(new InputMediaPhoto(InputFile.FromStream(stream, fileName)));
                                                            else if (type == "video")
                                                                mediaList.Add(new InputMediaVideo(InputFile.FromStream(stream, fileName)));
                                                        }
                                                        else
                                                        {
                                                            Logger.Error($"File not found: {localPath}");
                                                            await bot.SendMessage(chatId, $"Error: File not found: {FileIO.GetFileName(localPath)}");
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
                                            Logger.Warning($"Blocked file:// URL for user: {sender} - using official API");
                                            continue;
                                        }


                                        if (mediaList.Count > 0)
                                        {
                                            // Build full text with optional signature
                                            string? fullText = null;
                                            bool hasText = !string.IsNullOrEmpty(textContent);
                                            if (hasText)
                                            {
                                                fullText = textContent;
                                                if (signatureActive)
                                                    fullText += $"\n\nSent by {sender}";
                                            }
                                            else if (signatureActive)
                                            {
                                                fullText = $"Sent by {sender}";
                                            }

                                            // Prepare caption and leftover text respecting limits
                                            string? firstCaption = null;
                                            string? leftoverText = null;
                                            if (!string.IsNullOrEmpty(fullText))
                                            {
                                                if (fullText.Length <= MaxCaptionLength)
                                                {
                                                    firstCaption = fullText;
                                                }
                                                else
                                                {
                                                    firstCaption = new string(SplitText(fullText, MaxCaptionLength).FirstOrDefault()?.ToCharArray() ?? Array.Empty<char>());
                                                    leftoverText = fullText.Substring(firstCaption.Length).TrimStart();
                                                }
                                            }

                                            // Send media in batches of up to 10
                                            int total = mediaList.Count;
                                            int sent = 0;
                                            int batchIndex = 0;
                                            while (sent < total)
                                            {
                                                var batch = mediaList.Skip(sent).Take(MaxMediaPerGroup).ToList();
                                                // Apply caption only to the first media of the first batch
                                                if (batchIndex == 0 && !string.IsNullOrEmpty(firstCaption))
                                                {
                                                    if (batch[0] is InputMediaPhoto photo)
                                                        photo.Caption = firstCaption;
                                                    else if (batch[0] is InputMediaVideo video)
                                                        video.Caption = firstCaption;
                                                }
                                                else if (batchIndex == 0 && string.IsNullOrEmpty(firstCaption) && !hasText && signatureActive)
                                                {
                                                    // No text, but signature active -> simple caption on first item
                                                    if (batch[0] is InputMediaPhoto photo)
                                                        photo.Caption = $"Sent by {sender}";
                                                    else if (batch[0] is InputMediaVideo video)
                                                        video.Caption = $"Sent by {sender}";
                                                }

                                                try
                                                {
                                                    var response = await bot.SendMediaGroup(chatId, batch);
                                                    if (response == null)
                                                    {
                                                        Logger.Warning("Telegram did not return any media messages. Files not deleted.");
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    Logger.Error($"Failed to send media group: {ex.Message}");
                                                }

                                                sent += batch.Count;
                                                batchIndex++;
                                            }

                                            // After sending media groups, send any leftover text as plain messages (4096 limit)
                                            if (!string.IsNullOrEmpty(leftoverText))
                                            {
                                                foreach (var chunk in SplitText(leftoverText, MaxTextLength))
                                                {
                                                    await bot.SendMessage(chatId, chunk);
                                                }
                                            }

                                            // Cleanup any local temp files
                                            if (filesToDelete.Count > 0)
                                            {
                                                foreach (var path in filesToDelete)
                                                {
                                                    try
                                                    {
                                                        if (FileIO.FileExists(path))
                                                            FileIO.DeleteFile(path);
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        Logger.Warning($"Failed to delete temp file '{path}': {ex.Message}");
                                                    }
                                                }
                                            }

                                            Logger.Info($"Sent {Math.Ceiling((double)total / MaxMediaPerGroup)} media group(s) to user: {sender}");
                                        }
                                        else if (!string.IsNullOrEmpty(textContent))
                                        {
                                            string message = textContent;
                                            if (signatureActive)
                                            {
                                                message += $"\n\nSent by {sender}";
                                            }

                                            // Split long text into 4096-sized chunks
                                            foreach (var chunk in SplitText(message, MaxTextLength))
                                            {
                                                await bot.SendMessage(chatId, chunk);
                                            }

                                            Logger.Info($"Sent text message(s) to user: {sender}");
                                        }
                                    }
                                    else if (jsonData.TryGetProperty("error", out JsonElement errorElement))
                                    {
                                        string? errorMessage = errorElement.GetString();
                                        await bot.SendMessage(chatId, $"{errorMessage}");
                                        Logger.Info($"Sent error message to user: {sender}");
                                    }
                                    else
                                    {
                                        Logger.Warning("Error: Unsupported data type.");
                                    }
                                }
                                else
                                {
                                    Logger.Warning("Error: Unsupported data type.");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("JSON processing error: " + ex.Message);
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
