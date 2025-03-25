using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TikTok_bot
{
    /// <summary>
    /// Класс, представляющий статус пользователя.
    /// </summary>
    public class UserStatus
    {
        /// <summary>
        /// Уникальный идентификатор пользователя.
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// Флаг активности пользователя.
        /// </summary>
        public bool IsActive { get; set; }
    }
}

