using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;


class Program
{
    static readonly ITelegramBotClient bot = new TelegramBotClient(Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"));
    //Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
    static readonly List<WebSocket> clients = new();
    static readonly Dictionary<WebSocket, string> clientPlatforms = new();
    static readonly Dictionary<WebSocket, long> clientChatIds = new();
    static readonly Dictionary<WebSocket, Update?> clientUpdates = new();

    static async Task Main()
    {
        Console.WriteLine("OwnDownloader tgbot v. A2");
        bot.StartReceiving(UpdateHandler, ErrorHandler);
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Bot started!");

        HttpListener listener = new();
        listener.Prefixes.Add("http://*:8098/");
        //"http://localhost:8098/" "http://*:8098/"
        listener.Start();
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - WebSocket server active");

        while (true)
        {
            HttpListenerContext context = await listener.GetContextAsync();
            if (context.Request.IsWebSocketRequest)
            {
                HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(null);
                WebSocket ws = wsContext.WebSocket;
                _ = HandleWebSocket(ws);
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Invalid HTTP request received and closed with 400 status.");
            }
        }
    }

    static async Task HandleWebSocket(WebSocket ws)
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

                                    foreach (var item in mediaElement.EnumerateArray()) // Перебираем все элементы массива
                                    {
                                        if (item.ValueKind == JsonValueKind.Object)
                                        {
                                            string? type = item.GetProperty("type").GetString();
                                            string? url = item.GetProperty("url").GetString();

                                            if (!string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(url))
                                            {
                                                if (type == "photo") // Если тип "фото" и ссылки есть 
                                                {
                                                    mediaList.Add(new InputMediaPhoto(InputFile.FromUri(url)));  // Ложим в наш массив медиа
                                                }
                                                else if (type == "video") // Если тип "видео" и ссылки есть
                                                {
                                                    mediaList.Add(new InputMediaVideo(InputFile.FromUri(url)));
                                                }
                                            }
                                        }
                                    }

                                    if (mediaList.Count > 0)
                                    {
                                        // Устанавливаем подпись для первого медиа
                                        if (mediaList[0] is InputMediaPhoto photoMedia) 
                                        {
                                            photoMedia.Caption = $"Sent by {sender}"; 
                                        }
                                        else if (mediaList[0] is InputMediaVideo videoMedia)
                                        {
                                            videoMedia.Caption = $"Sent by {sender}";
                                        }

                                        // Отправляем все медиа в одной медиагруппе
                                        await bot.SendMediaGroup(chatId, mediaList);
                                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Sent media group to user: {sender}"); 
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

    static async Task UpdateHandler(ITelegramBotClient botClient, Update update, CancellationToken token) 
    {
        if (update.Message?.Text != null)
        {
            string messageText = update.Message.Text;
            long chatId = update.Message.Chat.Id;
            string sender = update.Message.From?.Username ?? update.Message.From?.FirstName ?? "Пользователь";

            string tiktokLink = ExtractTikTokUrl(messageText); // Проверяем ссылку на предмет тиктока
            string instagramLink = ExtractInstagramUrl(messageText); // И Инстаграма

            if (!string.IsNullOrEmpty(tiktokLink) || !string.IsNullOrEmpty(instagramLink)) // Если есть ссылка на TikTok или Instagram
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - User: {sender}, Link: {messageText}");

                foreach (var ws in clients) // Перебираем всех клиентов и ищем нужный
                {
                    if (ws.State == WebSocketState.Open) 
                    {
                        if (clientPlatforms.TryGetValue(ws, out string? platform)) 
                        {
                            if (!string.IsNullOrEmpty(tiktokLink) && platform == "tiktok") // Если платформа тикток и ссылка есть
                            {
                                await SendLinkToClient(ws, tiktokLink, chatId, update); // Шлем на ТТлинкер
                            }
                            else if (!string.IsNullOrEmpty(instagramLink) && platform == "instagram") // Если платформа инстаграм и ссылка есть
                            {
                                await SendLinkToClient(ws, instagramLink, chatId, update); // Шлем на ИГлинкер
                            }
                        }
                    }
                }
            }
        }
    }

    static async Task SendLinkToClient(WebSocket ws, string link, long chatId, Update update) // Функция отправки в вебсокет клиенту
    {
        clientChatIds[ws] = chatId;
        clientUpdates[ws] = update; 
        byte[] data = Encoding.UTF8.GetBytes(link);
        await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Sent link to {clientPlatforms[ws]} client for chat ID: {chatId}");
    }

    static Task ErrorHandler(ITelegramBotClient botClient, Exception exception, CancellationToken token)
    {
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Error: " + exception.Message);
        return Task.CompletedTask;
    }

    static string ExtractTikTokUrl(string text)
    {
        Regex regex = new(@"https?:\/\/(www\.)?(vm\.tiktok\.com\/[A-Za-z0-9]+\/?|tiktok\.com\/@[A-Za-z0-9_.-]+\/video\/\d+)", RegexOptions.IgnoreCase);
        Match match = regex.Match(text);
        return match.Success ? match.Value.Trim() : "";
    }

    static string ExtractInstagramUrl(string text) // Функция для извлечения ссылки на Инстаграм, аналогичная тиктокам
    {
        Regex regex = new(@"https?:\/\/(www\.)?instagram\.com\/[A-Za-z0-9_\.\-\/\?\=&]+", RegexOptions.IgnoreCase);
        Match match = regex.Match(text);
        return match.Success ? match.Value.Trim() : "";
    }
    static async Task<string> ReceiveFullMessage(WebSocket ws, byte[] buffer) // Функция для получения сообщения с клиента вебсокета, ибо размера массива порой не хватает
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
}
