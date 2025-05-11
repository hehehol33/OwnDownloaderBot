using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot.Types;
using System.Text.Json;
using Telegram.Bot;

namespace TikTok_bot
{
    /// <summary>
    /// Класс, предназначенный для обработки WebSocket соединений и отправки данных клиентам.
    /// </summary>
    internal class WebSocketServer
    {
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
        /// Обрабатывает подключение WebSocket клиента, получает и отправляет данные через WebSocket.
        /// </summary>
        /// <param name="filePath">Путь к файлу для загрузки пользовательских данных.</param>
        /// <param name="ws">WebSocket соединение клиента.</param>
        /// <param name="clients">Список всех подключенных WebSocket клиентов.</param>
        /// <param name="clientPlatforms">Словарь, связывающий WebSocket с платформой клиента.</param>
        /// <param name="clientChatIds">Словарь, связывающий WebSocket с идентификатором чата клиента.</param>
        /// <param name="clientUpdates">Словарь, связывающий WebSocket с обновлениями от клиента.</param>
        /// <param name="bot">Объект Telegram бота для отправки сообщений и медиа.</param>
        /// <returns>Задача, представляющая асинхронную операцию обработки соединения.</returns>
        public static async Task HandleWebSocket(
            string filePath,
            WebSocket ws,
            List<WebSocket> clients,
            Dictionary<WebSocket, string> clientPlatforms,
            Dictionary<WebSocket, long> clientChatIds,
            Dictionary<WebSocket, Update?> clientUpdates,
            ITelegramBotClient bot)
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
                                // Поменял часть на обработку и отправку, теперь работает как с пачкой медиа
                                string sender = update.Message?.From?.Username ?? update.Message?.From?.FirstName ?? "Пользователь";
                                var jsonData = JsonSerializer.Deserialize<JsonElement>(receivedData);
                                if (jsonData.ValueKind == JsonValueKind.Object)
                                {
                                    if (jsonData.TryGetProperty("media", out JsonElement mediaElement) && mediaElement.ValueKind == JsonValueKind.Array) // Если это тип "медиа" и это массив
                                    {
                                        var mediaList = new List<IAlbumInputMedia>();
                                        string textContent = null;

                                        foreach (var item in mediaElement.EnumerateArray()) // Перебираем все элементы массива
                                        {
                                            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out JsonElement typeElement))
                                            {
                                                string type = typeElement.GetString();

                                                if (type == "photo" && item.TryGetProperty("url", out JsonElement photoUrlElement)) // Если тип "фото" и ссылки есть 
                                                {
                                                    string url = photoUrlElement.GetString();
                                                    if (!string.IsNullOrEmpty(url))
                                                    {
                                                        mediaList.Add(new InputMediaPhoto(InputFile.FromUri(url)));  // Ложим в наш массив медиа
                                                    }
                                                }
                                                else if (type == "video" && item.TryGetProperty("url", out JsonElement videoUrlElement)) // Если тип "видео" и ссылки есть
                                                {
                                                    string url = videoUrlElement.GetString();
                                                    if (!string.IsNullOrEmpty(url))
                                                    {
                                                        mediaList.Add(new InputMediaVideo(InputFile.FromUri(url)));
                                                    }
                                                }
                                                else if (type == "text" && item.TryGetProperty("content", out JsonElement contentElement)) // Если тип "текст" и контент есть
                                                {
                                                    textContent = contentElement.GetString();
                                                }
                                            }
                                        }

                                        // Проверка активности пользователя для подписи
                                        UserStorage userStorage = new UserStorage(filePath);
                                        List<UserStatus> users = userStorage.LoadUsers();
                                        long userIdToFind = update.Message.Chat.Id;
                                        UserStatus user = users.FirstOrDefault(u => u.Id == userIdToFind);
                                        bool isActive = user?.IsActive ?? false;

                                        // Отправляем медиа с подписью, если нужно
                                        if (mediaList.Count > 0)
                                        {
                                            // Если есть текст и медиа - добавляем текст как подпись к первому медиа
                                            if (!string.IsNullOrEmpty(textContent))
                                            {
                                                string caption = textContent;
                                                if (isActive)
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
                                            // Если только медиа и пользователь активен - добавляем подпись
                                            else if (isActive)
                                            {
                                                if (mediaList[0] is InputMediaPhoto photoMedia)
                                                {
                                                    photoMedia.Caption = $"Sent by {sender}";
                                                }
                                                else if (mediaList[0] is InputMediaVideo videoMedia)
                                                {
                                                    videoMedia.Caption = $"Sent by {sender}";
                                                }
                                            }

                                            // Отправляем все медиа в одной медиагруппе
                                            await bot.SendMediaGroup(chatId, mediaList);
                                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Sent media group to user: {sender}");
                                        }
                                        // Если есть только текст, отправляем его отдельным сообщением
                                        else if (!string.IsNullOrEmpty(textContent))
                                        {
                                            string message = textContent;
                                            if (isActive)
                                            {
                                                message += $"\n\nSent by {sender}";
                                            }
                                            
                                            await bot.SendMessage(chatId, message);
                                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Sent text message to user: {sender}");
                                        }
                                    }
                                    else if (jsonData.TryGetProperty("error", out JsonElement errorElement)) // Если это пришла ошибка, говорим пользователю
                                    {
                                        string? errorMessage = errorElement.GetString();
                                        await bot.SendMessage(chatId, $"{errorMessage}");
                                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Sent error message to user: {sender}");
                                    }
                                    else
                                    {
                                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Error: Unsupported data type."); // Если чортишо
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
            finally // Закрываем соединение и чистим данные
            {
                clients.Remove(ws);
                clientPlatforms.Remove(ws);
                clientChatIds.Remove(ws);
                clientUpdates.Remove(ws);
            }
        }
    }
}
