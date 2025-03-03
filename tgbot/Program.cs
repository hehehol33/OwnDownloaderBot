using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using System.Collections.Generic;

class Program
{
    static ITelegramBotClient bot = new TelegramBotClient(Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"));
    static List<WebSocket> clients = new List<WebSocket>();
    static Dictionary<WebSocket, long> clientChatIds = new Dictionary<WebSocket, long>();

    static async Task Main()
    {
        bot.StartReceiving(UpdateHandler, ErrorHandler);
        Console.WriteLine("Бот запущен!");

        HttpListener listener = new HttpListener();
        listener.Prefixes.Add("http://*:8098/");
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
        byte[] buffer = new byte[1024];
        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            string videoUrl = Encoding.UTF8.GetString(buffer, 0, result.Count);
            Console.WriteLine("📥 Получена ссылка на видео: " + videoUrl);

            if (clientChatIds.TryGetValue(ws, out long chatId))
            {
                await bot.SendVideoAsync(chatId, videoUrl, caption: "Вот ваше видео с TikTok");
            }
        }
        clients.Remove(ws);
        clientChatIds.Remove(ws);
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
