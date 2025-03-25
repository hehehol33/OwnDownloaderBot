using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TikTok_bot
{
    /// <summary>
    /// Класс для работы с JSON-файлом, содержащим статусы пользователей.
    /// </summary>
    public class UserStorage
    {
        private readonly string _filePath;

        /// <summary>
        /// Инициализирует новый экземпляр класса UserStorage.
        /// </summary>
        /// <param name="filePath">Путь к JSON-файлу.</param>
        public UserStorage(string filePath)
        {
            _filePath = filePath;
        }

        /// <summary>
        /// Загружает список пользователей из JSON-файла.
        /// </summary>
        /// <returns>Список пользователей.</returns>
        public List<UserStatus> LoadUsers()
        {
            if (File.Exists(_filePath))
            {
                string jsonString = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<List<UserStatus>>(jsonString) ?? new List<UserStatus>();
            }
            return new List<UserStatus>();
        }

        /// <summary>
        /// Сохраняет список пользователей в JSON-файл.
        /// </summary>
        /// <param name="users">Список пользователей для сохранения.</param>
        public void SaveUsers(List<UserStatus> users)
        {
            string jsonString = JsonSerializer.Serialize(users, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_filePath, jsonString);
        }

        /// <summary>
        /// Добавляет нового пользователя в JSON-файл.
        /// </summary>
        /// <param name="id">Идентификатор пользователя.</param>
        /// <param name="isActive">Флаг активности пользователя.</param>
        public void AddUser(long id, bool isActive)
        {
            var users = LoadUsers();
            users.Add(new UserStatus { Id = id, IsActive = isActive });
            SaveUsers(users);
        }
    }

}
