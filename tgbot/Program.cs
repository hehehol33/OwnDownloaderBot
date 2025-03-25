using System.Net;
using System.Net.WebSockets;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using TikTok_bot;


class Program
{
    const string filePath = "settings.json";
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

        // Устанавливаем команды для бота
        await BotCommands.SetCommandsAsync(bot);

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
                _ = WebSocketServer.HandleWebSocket(filePath, ws, clients, clientPlatforms, clientChatIds, clientUpdates, bot);
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Invalid HTTP request received and closed with 400 status.");
            }
        }
    }
    static async Task UpdateHandler(ITelegramBotClient botClient, Update update, CancellationToken token)
    {
        if (update.Message?.Text != null)
        {

            string messageText = update.Message.Text;
            long chatId = update.Message.Chat.Id;
            string sender = update.Message.From?.Username ?? update.Message.From?.FirstName ?? "Пользователь";
            if (messageText.StartsWith("/changeview"))
            {
                UserStorage userStorage = new UserStorage(filePath);
                List<UserStatus> users = userStorage.LoadUsers();

                UserStatus user = users.FirstOrDefault(u => u.Id == chatId);
                if (user == null)
                {
                    userStorage.AddUser(chatId, true);
                    await bot.SendMessage(chatId, "Signature status is on.");
                }
                else
                {
                    user.IsActive = !user.IsActive;
                    userStorage.SaveUsers(users);
                    await bot.SendMessage(chatId, user.IsActive ? "Signature status is on." : "Signature status is off.");
                }
            }

            string tiktokLink = NecessaryRegex.ExtractTikTokUrl(messageText); // Проверяем ссылку на предмет тиктока
            string instagramLink = NecessaryRegex.ExtractInstagramUrl(messageText); // И Инстаграма

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
                                await Sending.SendLinkToClient(ws, tiktokLink, chatId, update, clientPlatforms, clientChatIds, clientUpdates); // Шлем на ТТлинкер
                            }
                            else if (!string.IsNullOrEmpty(instagramLink) && platform == "instagram") // Если платформа инстаграм и ссылка есть
                            {
                                await Sending.SendLinkToClient(ws, instagramLink, chatId, update, clientPlatforms, clientChatIds, clientUpdates); // Шлем на ИГлинкер
                            }
                        }
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

}
