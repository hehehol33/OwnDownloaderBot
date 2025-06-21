import WebSocketCommunicator from "./communicator.js";
import Logger from "./logger.js";

// Константи для налаштування API запитів
const CONFIG = {
  API_RETRY_COUNT: 1,             // Кількість повторних спроб (обмежено до 1)
  API_RETRY_DELAY: 500,           // Затримка між спробами (мс)
  API_REQUEST_DELAY: 300,         // Затримка перед запитом (мс)
  VERSION: "A5"                   // Версія
};

const logger = new Logger({ appName: 'tiktok.linker' });
logger.info(`ttlinker v. ${CONFIG.VERSION}`);

// Спрощена черга повідомлень
class MessageQueue {
  constructor() {
    this.queue = [];
    this.processing = false;
  }

  // Додати нове повідомлення до черги
  async enqueue(message, processCallback) {
    return new Promise((resolve, reject) => {
      logger.debug(`Adding message to queue, current length: ${this.queue.length}`);
      this.queue.push({ message, processCallback, resolve, reject });
      this.processNext();
    });
  }

  // Обробити наступне повідомлення в черзі
  async processNext() {
    // Якщо зараз щось обробляється або черга порожня, вийти
    if (this.processing || this.queue.length === 0) return;
    
    this.processing = true;
    const { message, processCallback, resolve, reject } = this.queue.shift();
    logger.debug(`Processing next message in queue, remaining: ${this.queue.length}`);
    
    try {
      // Виклик callback-функції для обробки повідомлення
      const result = await processCallback(message);
      resolve(result);
    } catch (error) {
      logger.error(`Error processing message: ${error.message}`);
      reject(error);
    } finally {
      this.processing = false;
      // Обробка наступного повідомлення в черзі
      this.processNext();
    }
  }
}

// Створюємо глобальну чергу повідомлень
const messageQueue = new MessageQueue();

// Функція затримки
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

// Отримання даних з TikTok з обробкою помилок і повторними спробами
async function fetchTikTokData(tiktokUrl, retries = CONFIG.API_RETRY_COUNT, delay = CONFIG.API_RETRY_DELAY) {
  try {
    logger.debug(`Fetching data for: ${tiktokUrl}`);
    
    // Додаємо затримку перед запитом до API
    await sleep(CONFIG.API_REQUEST_DELAY);
    
    const response = await fetch(`https://tikwm.com/api?url=${encodeURIComponent(tiktokUrl)}`);
    
    if (!response.ok) {
      logger.error(`API Request failed with status ${response.status}`);
      throw new Error(`API Request failed with status ${response.status}`);
    }
    
    const responseData = await response.json();
    
    // Спрощений варіант перевірки
    if (!responseData.data) {
      throw new Error("Empty API response");
    }

    const { data } = responseData;
    const hasMedia = (data.play && data.play.length > 0) || 
                     (Array.isArray(data.images) && data.images.length > 0);
                     
    if (!hasMedia) {
      logger.warn(`No valid media in API response for ${tiktokUrl}`);
      throw new Error("No content found in API response");
    }
    
    logger.debug("API response received successfully");
    return responseData.data;
    
  } catch (error) {
    if (retries <= 0) {
      logger.error(`All retry attempts failed for ${tiktokUrl}`);
      throw error;
    }
    
    logger.warn(`Retrying fetch for ${tiktokUrl}, attempts left: ${retries}, reason: ${error.message}`);
    
    // Збільшуємо затримку з кожною спробою (експоненційний backoff)
    const backoffDelay = delay * (CONFIG.API_RETRY_COUNT + 1 - retries);
    logger.debug(`Waiting ${backoffDelay}ms before retry`);
    await sleep(backoffDelay);
    
    return fetchTikTokData(tiktokUrl, retries - 1, delay);
  }
}

// Обробка TikTok посилання
async function processTiktokLink(url) {
  logger.info(`Processing link: ${url}`);
  
  try {
    const result = await fetchTikTokData(url);
    
    if (Array.isArray(result?.images) && result.images.length > 0) { 
      logger.info(`Found ${result.images.length} images`);
      
      // Оптимізувати маппінг зображень
      const mediaArray = [];
      const maxImages = Math.min(result.images.length, 10);

      for (let i = 0; i < maxImages; i++) {
        mediaArray.push({ type: "photo", url: result.images[i] });
      }
      
      communicator.send(JSON.stringify({ media: mediaArray }));
      logger.info(`Successfully sent ${mediaArray.length} images to client`);
      return { success: true, mediaCount: mediaArray.length };
    } else if (result?.play) {
      logger.info("Found video content");
      
      communicator.send(JSON.stringify({
        media: [{ type: "video", url: result.play }],
      }));
      logger.info("Successfully sent video to client");
      return { success: true, mediaType: "video" };
    } else {
      logger.warn("Content wasn't found");
      communicator.send(JSON.stringify({ error: "Content wasn't found" }));
      return { success: false, error: "Content wasn't found" };
    }
  } catch (error) {
    logger.error("Error processing request:", error.message);
    
    communicator.send(JSON.stringify({
      error: "Error processing request",
      details: error.message,
    }));
    
    throw error;
  }
}

// Створення та налаштування комунікатора
const communicator = new WebSocketCommunicator()
  .autoDetectConfig()
  .onMessage(async (tiktok_url) => {
    const url = typeof tiktok_url === 'string' ? tiktok_url : tiktok_url.toString();
    logger.info("Received link:", url);
    
    try {
      await messageQueue.enqueue(url, processTiktokLink);
    } catch (error) {
      logger.error("Failed to process message in queue:", error.message);
    }
  })
  .connect("platform:tiktok");