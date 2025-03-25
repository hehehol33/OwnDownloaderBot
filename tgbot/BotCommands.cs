using Telegram.Bot;
using Telegram.Bot.Types;

namespace TikTok_bot
{
    /// <summary>
    /// Устанавливает доступные команды для бота. Этот метод регистрирует команды бота в Telegram API, позволяя пользователям взаимодействовать с ботом
    /// с помощью предустановленных команд
    /// </summary>
    /// <param name="botClient">Telegram-клиент, используемый для взаимодействия с API Telegram.</param>
    /// <returns>Задача, представляющая асинхронную операцию.</returns>
    internal class BotCommands
    {
        public static async Task SetCommandsAsync(ITelegramBotClient botClient)
        {
            var commands = new[]
            {
            new BotCommand { Command = "changeview", Description = "Change signature status" }
        };

            await botClient.SetMyCommands(commands);
        }
    }
}
