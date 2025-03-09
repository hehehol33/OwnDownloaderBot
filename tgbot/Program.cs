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
    static readonly Dictionary<WebSocket, long> clientChatIds = new();
    static readonly Dictionary<WebSocket, Update?> clientUpdates = new();

    static async Task Main()
    {
        Console.WriteLine("OwnDownloader tgbot v. A1"); // Теперь будем писать версию
        bot.StartReceiving(UpdateHandler, ErrorHandler);
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Bot started!"); // Попереводил вывод на англ, а то у меня нема языкового пакета и смотреть вопросики не особо удобно

        HttpListener listener = new();
        listener.Prefixes.Add("*:8098/");
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
                clients.Add(ws);
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - WebSocket client connected");
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
        byte[] buffer = new byte[4096];
        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) // Добавил штатное отключение ттлинкера от сервера
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - WebSocket client disconnected");
                break;
            }
            string receivedData = Encoding.UTF8.GetString(buffer, 0, result.Count);
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Received data: " + receivedData);

            if (clientChatIds.TryGetValue(ws, out long chatId))
            {
                try
                {
                    if (clientUpdates.TryGetValue(ws, out Update? update) && update != null)
                    {
                        string sender = update.Message?.From?.Username ?? update.Message?.From?.FirstName ?? "Пользователь";
                        var jsonData = JsonSerializer.Deserialize<JsonElement>(receivedData);

                        if (jsonData.ValueKind == JsonValueKind.String) // Если пришла одна строка – это видео
                        {
                            receivedData = receivedData.Trim('"');
                            await bot.SendVideo(chatId, receivedData, caption: $"Отправил {sender}"); // Асинк уже нинадо явно прописывать, то ИИшка еще не знает
                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Sent video to user: {sender}"); // Логируем в консоль отправку видео
                        }
                        else if (jsonData.ValueKind == JsonValueKind.Array) // Если пришел массив – это фото
                        {

                            var urls = jsonData.EnumerateArray()
                                .Select(x => x.GetString())
                                .Where(x => !string.IsNullOrEmpty(x))
                                .ToList();

                            if (urls.Count > 0)
                            {
                                var media = urls.Select(url => new InputMediaPhoto(InputFile.FromUri(url!))).ToArray(); // Внес проверку на нулл 

                                media[0].Caption = $"Отправил {sender}";

                                await bot.SendMediaGroup(chatId, media); // Тож асинк убрал
                                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Sent photos to user: {sender}"); // Пишем в консось что отправили фоточки дяде
                            }
                        }
                        else if (jsonData.ValueKind == JsonValueKind.Object && jsonData.TryGetProperty("error", out JsonElement errorElement)) // Если пришла ошибка от ттлинкера (теперь он так умеет)
                        {

                            string? errorMessage = errorElement.GetString();
                            //string? errorDetails = jsonData.GetProperty("details").GetString(); // Если захочешь дебажить прямо в боте)
                            await bot.SendMessage(chatId, $"{errorMessage}"); // Даем понять шо это не бот висит, а разрабы дауны
                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Sent error message to user: {sender}"); // Хвастаемся этим в консоли
                        }
                        else
                        {
                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Error: Unsupported data type."); // Если хер пойми шо ттлинкер прислал
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - JSON processing error: " + ex.Message);
                }
            }
        }
        clients.Remove(ws);
        clientChatIds.Remove(ws);
        clientUpdates.Remove(ws);
    }

    static async Task UpdateHandler(ITelegramBotClient botClient, Update update, CancellationToken token)
    {
        if (update.Message?.Text != null)
        {
            if (update.Message.Text.Contains("https://www.tiktok.com") || update.Message.Text.Contains("https://vm.tiktok.com"))
            {
                string tiktokLink = ExtractTikTokUrl(update.Message.Text);
                long chatId = update.Message.Chat.Id;
                string sender = update.Message.From?.Username ?? update.Message.From?.FirstName ?? "Пользователь";
                
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - User: {sender}, Link: {tiktokLink}"); // Теперь будет выводить кто отправил ссылку

                foreach (var ws in clients)
                {
                    if (ws.State == WebSocketState.Open)
                    {
                        clientChatIds[ws] = chatId;
                        clientUpdates[ws] = update;
                        byte[] data = Encoding.UTF8.GetBytes(tiktokLink);
                        await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Sent TikTok link to WebSocket client for chat ID: {chatId}");
                    }
                }
            }
        }
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
}
