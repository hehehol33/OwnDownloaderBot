using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot.Types;

namespace TikTok_bot
{
    /// <summary>
    /// Класс, предназначенный для отправки данных через WebSocket клиентам.
    /// </summary>
    internal class Sending
    {
        /// <summary>
        /// Отправляет ссылку через WebSocket клиенту.
        /// </summary>
        /// <param name="ws">WebSocket, через который отправляется ссылка.</param>
        /// <param name="link">Ссылка, которая отправляется клиенту.</param>
        /// <param name="chatId">Идентификатор чата Telegram для отправки.</param>
        /// <param name="update">Обновление, содержащее информацию о сообщении пользователя.</param>
        /// <param name="clientPlatforms">Словарь, содержащий платформы для каждого клиента WebSocket.</param>
        /// <param name="clientChatIds">Словарь, содержащий идентификаторы чатов для каждого клиента WebSocket.</param>
        /// <param name="clientUpdates">Словарь, содержащий обновления для каждого клиента WebSocket.</param>
        /// <returns>Задача, представляющая асинхронную операцию отправки данных.</returns>
        public static async Task SendLinkToClient(
            WebSocket ws,
            string link,
            long chatId,
            Update update,
            Dictionary<WebSocket, string> clientPlatforms,
            Dictionary<WebSocket, long> clientChatIds,
            Dictionary<WebSocket, Update?> clientUpdates)
        {
            // Обновляем словари с информацией о клиенте
            clientChatIds[ws] = chatId;
            clientUpdates[ws] = update;

            // Преобразуем ссылку в байты и отправляем через WebSocket
            byte[] data = Encoding.UTF8.GetBytes(link);
            await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);

            // Логируем информацию о том, что ссылка была отправлена
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Sent link to {clientPlatforms[ws]} client for chat ID: {chatId}");
        }
      
    }
}
