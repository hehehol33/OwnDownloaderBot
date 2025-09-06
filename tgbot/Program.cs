using System.Net;
using System.Net.WebSockets;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using TikTok_bot;


class Program
{
    const string filePath = "settings.json";

    const string VERSION = "A7";
    
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

    // --- FileIO Configuration ---
    const string DEFAULT_DOWNLOAD_FOLDER = "C:\\testfolder"; 
    static readonly string DOWNLOAD_FOLDER = Environment.GetEnvironmentVariable("DOWNLOAD_FOLDER") ?? DEFAULT_DOWNLOAD_FOLDER; 
    const int STALE_FILE_TIMEOUT_SECONDS = 1800; // How old files need to be cleaned up
    const int CLEANUP_INTERVAL_SECONDS = 3600;  // How often to check for stale files   
    // ---------------------------------

    // --- Logging Configuration ---
    const string LOG_LEVEL_ENV_VAR = "LOG_LEVEL";
    const LogLevel DEFAULT_LOG_LEVEL = LogLevel.INFO;
    // -----------------------------
    
    // Initialize the bot with appropriate server (determined at runtime)
    static readonly ITelegramBotClient bot;
    static readonly bool isUsingLocalTGServer;
    
    // Static constructor to initialize bot with proper server
    static Program()
    {
        var botToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrEmpty(botToken))
        {
            Logger.Critical("TELEGRAM_BOT_TOKEN environment variable not set.");
            Environment.Exit(1); // Exit if token is not provided
        }

        // --- Attempt 1: Try to connect to the local server ---
        try
        {
            var localBot = new TelegramBotClient(
                new Telegram.Bot.TelegramBotClientOptions(botToken, baseUrl: TGSERVER_BASE_URL)
            );
            
            // Perform an API call to test the connection and token
            localBot.GetMe().GetAwaiter().GetResult();
            
            // If successful, set the bot and finish initialization
            bot = localBot;
            isUsingLocalTGServer = true;
            return; // Successfully connected to local server
        }
        catch (Exception ex)
        {
            Logger.Warning($"Could not connect to local Telegram API server. Reason: {ex.Message}");
            // The local server might be down, or the token is invalid.
            // Now, we fall back to the official server to check the token.
        }

        // --- Attempt 2: Fallback to the official server ---
        try
        {
            var officialBot = new TelegramBotClient(botToken);
            
            // Perform an API call to test the token against the official server
            officialBot.GetMe().GetAwaiter().GetResult();

            // If successful, set the bot and finish initialization
            bot = officialBot;
            isUsingLocalTGServer = false;
        }
        catch (ApiRequestException apiEx)
        {
            // This specifically catches errors from the Telegram API (e.g., 401 Unauthorized)
            Logger.Critical($"Failed to connect to official Telegram API. The token is likely invalid. Error {apiEx.ErrorCode}: {apiEx.Message}");
            Environment.Exit(1); // Exit because the token is invalid
        }
        catch (Exception ex)
        {
            // This catches other errors like network issues
            Logger.Critical($"A critical error occurred while connecting to the official Telegram API: {ex.Message}");
            Environment.Exit(1); // Exit on other critical errors
        }
    }
    
    static readonly List<WebSocket> clients = [];
    static readonly Dictionary<WebSocket, string> clientPlatforms = [];
    static readonly Dictionary<WebSocket, long> clientChatIds = [];
    static readonly Dictionary<WebSocket, Update?> clientUpdates = [];
          
    static async Task Main()
    {
        // Configure Logger from environment variable or use default
        var logLevelStr = Environment.GetEnvironmentVariable(LOG_LEVEL_ENV_VAR);
        if (!Enum.TryParse<LogLevel>(logLevelStr, true, out var configuredLogLevel))
        {
            configuredLogLevel = DEFAULT_LOG_LEVEL;
        }
        Logger.Configure(configuredLogLevel);

        Logger.Info($"OwnDownloader tgbot v. {VERSION}");

        bot.StartReceiving(UpdateHandler, ErrorHandler);
        Logger.Info("Bot started!");
        
        if (isUsingLocalTGServer)
        {
            Logger.Info($"Connected to local Telegram Bot API Server: {TGSERVER_BASE_URL}");
        }
        else
        {
            Logger.Info("Failed to connect to local server, using official Telegram Bot API Server");
        }

        // Create download directory if it doesn't exist
        if (!Directory.Exists(DOWNLOAD_FOLDER))
        {
            Directory.CreateDirectory(DOWNLOAD_FOLDER);
            Logger.Info($"Created download directory: {DOWNLOAD_FOLDER}");
        }

        // Start the file cleanup task
        FileIO.StartFileCleanupTask(
            DOWNLOAD_FOLDER,
            "*.*",
            STALE_FILE_TIMEOUT_SECONDS,
            CLEANUP_INTERVAL_SECONDS);

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
        Logger.Info($"WebSocket server active on port {wsPort}");

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
                Logger.Warning("Invalid HTTP request received and closed with 400 status.");
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
                Logger.Info($"User: {sender}, Link: {messageText}");

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
        Logger.Error("Error: " + exception.Message);
        return Task.CompletedTask;
    }
}