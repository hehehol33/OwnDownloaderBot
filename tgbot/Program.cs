using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;

class Program
{
    static ITelegramBotClient bot = new TelegramBotClient(Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"));
    //Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
    static List<WebSocket> clients = new List<WebSocket>();
    static Dictionary<WebSocket, long> clientChatIds = new Dictionary<WebSocket, long>();
    static Dictionary<WebSocket, Update> clientUpdates = new Dictionary<WebSocket, Update>();

    static async Task Main()
    {
        bot.StartReceiving(UpdateHandler, ErrorHandler);
        Console.WriteLine("Бот запущен!");

        HttpListener listener = new HttpListener();
        listener.Prefixes.Add("http://*:8098/");
        //"http://localhost:8098/" "http://*:8098/"
        listener.Start();
        Console.WriteLine("WebSocket сервер запущен");

        while (true)
        {
            HttpListenerContext context = await listener.GetContextAsync();
            if (context.Request.IsWebSocketRequest)
            {
                HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(null);
                WebSocket ws = wsContext.WebSocket;
                clients.Add(ws);
                Console.WriteLine("Клиент WebSocket подключился");
                _ = HandleWebSocket(ws);
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
            }
        }
    }

    static async Task HandleWebSocket(WebSocket ws)
    {
        byte[] buffer = new byte[4096];
        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            string receivedData = Encoding.UTF8.GetString(buffer, 0, result.Count);
            Console.WriteLine("Получены данные: " + receivedData);

            if (clientChatIds.TryGetValue(ws, out long chatId))
            {
                try
                {
                    clientUpdates.TryGetValue(ws, out Update update);
                    string sender = update.Message.From?.Username ?? update.Message.From?.FirstName ?? "Пользователь";
                    var jsonData = JsonSerializer.Deserialize<JsonElement>(receivedData);

                    if (jsonData.ValueKind == JsonValueKind.String)
                    {
                        receivedData = receivedData.Trim('"');
                        // Если пришла одна строка – это видео
                        await bot.SendVideoAsync(chatId, receivedData, caption: $"Отправил {sender}");
                    }
                    else if (jsonData.ValueKind == JsonValueKind.Array)
                    {
                        // Если пришел массив – это фото
                        var urls = jsonData.EnumerateArray()
                            .Select(x => x.GetString())
                            .Where(x => !string.IsNullOrEmpty(x))
                            .ToList();

                        if (urls.Count > 0)
                        {
                            var media = urls.Select(url => new InputMediaPhoto(InputFile.FromUri(url))).ToArray();

                            media[0].Caption = $"Отправил {sender}";

                            await bot.SendMediaGroupAsync(chatId, media);
                        }
                    }
                    else
                    {
                        Console.WriteLine("Ошибка: Неподдерживаемый формат данных.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Ошибка обработки JSON: " + ex.Message);
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

                foreach (var ws in clients)
                {
                    if (ws.State == WebSocketState.Open)
                    {
                        clientChatIds[ws] = chatId;
                        clientUpdates[ws] = update;
                        byte[] data = Encoding.UTF8.GetBytes(tiktokLink);
                        await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);


                    }
                }
            }
        }
    }

    static Task ErrorHandler(ITelegramBotClient botClient, Exception exception, CancellationToken token)
    {
        Console.WriteLine("Ошибка: " + exception.Message);
        return Task.CompletedTask;
    }

    static string ExtractTikTokUrl(string text)
    {
        Regex regex = new Regex(@"https?:\/\/(www\.)?(vm\.tiktok\.com\/[A-Za-z0-9]+\/?|tiktok\.com\/@[A-Za-z0-9_.-]+\/video\/\d+)", RegexOptions.IgnoreCase);
        Match match = regex.Match(text);
        return match.Success ? match.Value.Trim() : "";
    }
}
