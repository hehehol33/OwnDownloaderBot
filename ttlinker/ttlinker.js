const Tiktok = require("@tobyg74/tiktok-api-dl");
const WebSocket = require('ws');

const wsClient = new WebSocket('ws://tgbot:8098');

wsClient.on('open', () => {
  console.log('✅ Подключено к WebSocket серверу');
});

wsClient.on('message', async (message) => {
  const tiktok_url = message.toString();
  console.log("📥 Получена ссылка:", tiktok_url);

  try {
    Tiktok.Downloader(tiktok_url, {
      version: "v1",
      proxy: null,
      showOriginalResponse: false
    }).then((result) => {
      const videoUrl = result.result.video?.playAddr?.[0] || result.result.video?.downloadAddr?.[0];

      if (videoUrl) {
        console.log("🎥 Ссылка на скачивание видео:", videoUrl);
        wsClient.send(videoUrl);
      } else {
        console.log("❌ Видео не найдено");
      }
    }).catch((error) => {
      console.error("❌ Ошибка загрузки видео:", error);
    });
  } catch (error) {
    console.error("❌ Ошибка обработки запроса:", error);
  }
});

wsClient.on('error', (error) => {
  console.error('❌ Ошибка WebSocket:', error);
});