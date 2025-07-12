using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Telegram.Bot;
using Telegram.Bot.Types;
using TikTok_bot;


class Program
{
    const string filePath = "settings.json";
    
    // Default values for bot API configuration (without protocol prefix)
    const string DEFAULT_LOCAL_TGAPI_HOST = "localhost";
    const ushort DEFAULT_LOCAL_TGAPI_PORT = 8081;
    
    // Read from environment variables or use defaults
    static readonly string TGSERVER_HOST = Environment.GetEnvironmentVariable("TGSERVER_HOST") ?? DEFAULT_LOCAL_TGAPI_HOST;
    static readonly ushort TGSERVER_PORT = ushort.TryParse(Environment.GetEnvironmentVariable("TGSERVER_PORT"), out var port) 
        ? port 
        : DEFAULT_LOCAL_TGAPI_PORT;
    
    // Construct the base URL directly with the protocol prefix
    static readonly string TGSERVER_BASE_URL = $"http://{TGSERVER_HOST}:{TGSERVER_PORT}/bot";
    
    // Initialize the bot with appropriate server (determined at runtime)
    static readonly ITelegramBotClient bot;
    static readonly bool isUsingLocalTGServer;
    
    // Static constructor to initialize bot with proper server
    static Program()
    {
        // Try to connect to local server first
        try
        {
            var localBot = new TelegramBotClient(
                new Telegram.Bot.TelegramBotClientOptions(
                    Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"),
                    baseUrl: TGSERVER_BASE_URL
                )
            );
            
            // Perform a simple API call to test if server is working
            var me = localBot.GetMe().GetAwaiter().GetResult();
            
            // If we got here, local server is working
            bot = localBot;
            isUsingLocalTGServer = true;
        }
        catch
        {
            // If local server failed, use official API
            bot = new TelegramBotClient(Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"));
            isUsingLocalTGServer = false;
        }
    }
    
    static readonly List<WebSocket> clients = [];
    static readonly Dictionary<WebSocket, string> clientPlatforms = [];
    static readonly Dictionary<WebSocket, long> clientChatIds = [];
    static readonly Dictionary<WebSocket, Update?> clientUpdates = [];
          
    static async Task Main()
    {
        Console.WriteLine("OwnDownloader tgbot v. A5");

        bot.StartReceiving(UpdateHandler, ErrorHandler);
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Bot started!");
        
        if (isUsingLocalTGServer)
        {
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Connected to local Telegram Bot API Server: {TGSERVER_BASE_URL}");
        }
        else
        {
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Failed to connect to local server, using official Telegram Bot API Server");
        }

        // Устанавливаем команды для бота
        await BotCommands.SetCommandsAsync(bot);

        HttpListener listener = new();
        ushort wsPort = ushort.TryParse(Environment.GetEnvironmentVariable("PORT"), out var result) ? result : (ushort)8098;

        if (IsRunningInDocker())
        {
            listener.Prefixes.Add($"http://*:{wsPort}/");
        }
        else
        {
            listener.Prefixes.Add($"http://localhost:{wsPort}/");
        }

        listener.Start();
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - WebSocket server active on port {wsPort}");

        while (true)
        {
            HttpListenerContext context = await listener.GetContextAsync();
            if (context.Request.IsWebSocketRequest)
            {
                HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(null);
                WebSocket ws = wsContext.WebSocket;
                _ = WebSocketServer.HandleWebSocket(ws, clients, clientPlatforms, clientChatIds, clientUpdates, bot, isUsingLocalTGServer);
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Invalid HTTP request received and closed with 400 status.");
            }
        }
    }

    static bool IsRunningInDocker() 
    {
        return File.Exists("/.dockerenv");
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
                    await bot.SendMessage(chatId, "Signature status is on.", cancellationToken: token);
                }
                else
                {
                    user.IsActive = !user.IsActive;
                    userStorage.SaveUsers(users);
                    await bot.SendMessage(chatId, user.IsActive ? "Signature status is on." : "Signature status is off.", cancellationToken: token);
                }
            }

            string tiktokLink = NecessaryRegex.ExtractTikTokUrl(messageText);
            string instagramLink = NecessaryRegex.ExtractInstagramUrl(messageText);
            string youtubeLink = NecessaryRegex.ExtractYouTubeUrl(messageText);

            if (!string.IsNullOrEmpty(tiktokLink) || !string.IsNullOrEmpty(instagramLink) || !string.IsNullOrEmpty(youtubeLink))
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - User: {sender}, Link: {messageText}");

                foreach (var ws in clients)
                {
                    if (ws.State == WebSocketState.Open)
                    {
                        if (clientPlatforms.TryGetValue(ws, out string? platform))
                        {
                            if (!string.IsNullOrEmpty(tiktokLink) && platform == "tiktok")
                            {
                                await Sending.SendLinkToClient(ws, tiktokLink, chatId, update, clientPlatforms, clientChatIds, clientUpdates);
                            }
                            else if (!string.IsNullOrEmpty(instagramLink) && platform == "instagram")
                            {
                                await Sending.SendLinkToClient(ws, instagramLink, chatId, update, clientPlatforms, clientChatIds, clientUpdates);
                            }
                            else if (!string.IsNullOrEmpty(youtubeLink) && platform == "youtube")
                            {
                                await Sending.SendLinkToClient(ws, youtubeLink, chatId, update, clientPlatforms, clientChatIds, clientUpdates);
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