import WebSocketCommunicator from "./communicator.js";
import Logger from "./logger.js";

// API request configuration constants
const CONFIG = {
  API_RETRY_COUNT: 1,             // Number of retry attempts (limited to 1)
  API_RETRY_DELAY: 500,           // Delay between retries (ms)
  API_REQUEST_DELAY: 300,         // Delay before each request (ms)
  VERSION: "A5"                   // Version
};

const logger = new Logger({ appName: 'tiktok.linker' });
logger.info(`ttlinker v. ${CONFIG.VERSION}`);

// Simple message queue for sequential processing
class MessageQueue {
  constructor() {
    this.queue = [];
    this.processing = false;
  }

  // Add a new message to the queue
  async enqueue(message, processCallback) {
    return new Promise((resolve, reject) => {
      logger.debug(`Adding message to queue, current length: ${this.queue.length}`);
      this.queue.push({ message, processCallback, resolve, reject });
      this.processNext();
    });
  }

  // Process the next message in the queue
  async processNext() {
    // Exit if currently processing or queue is empty
    if (this.processing || this.queue.length === 0) return;
    
    this.processing = true;
    const { message, processCallback, resolve, reject } = this.queue.shift();
    logger.debug(`Processing next message in queue, remaining: ${this.queue.length}`);
    
    try {
      // Call the callback function to process the message
      const result = await processCallback(message);
      resolve(result);
    } catch (error) {
      logger.error(`Error processing message: ${error.message}`);
      reject(error);
    } finally {
      this.processing = false;
      // Process the next message in the queue
      this.processNext();
    }
  }
}

// Create a global message queue
const messageQueue = new MessageQueue();

// Delay function
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

// Fetch TikTok data with error handling and retries
async function fetchTikTokData(tiktokUrl, retries = CONFIG.API_RETRY_COUNT, delay = CONFIG.API_RETRY_DELAY) {
  try {
    logger.debug(`Fetching data for: ${tiktokUrl}`);
    
    // Add delay before API request
    await sleep(CONFIG.API_REQUEST_DELAY);
    
    const response = await fetch(`https://tikwm.com/api?url=${encodeURIComponent(tiktokUrl)}`);
    
    if (!response.ok) {
      logger.error(`API Request failed with status ${response.status}`);
      throw new Error(`API Request failed with status ${response.status}`);
    }
    
    const responseData = await response.json();
    
    // Simplified validation
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
    
    // Increase delay with each retry (exponential backoff)
    const backoffDelay = delay * (CONFIG.API_RETRY_COUNT + 1 - retries);
    logger.debug(`Waiting ${backoffDelay}ms before retry`);
    await sleep(backoffDelay);
    
    return fetchTikTokData(tiktokUrl, retries - 1, delay);
  }
}

// Process TikTok link
async function processTiktokLink(url) {
  logger.info(`Processing link: ${url}`);
  
  try {
    const result = await fetchTikTokData(url);
    
    if (Array.isArray(result?.images) && result.images.length > 0) { 
      logger.info(`Found ${result.images.length} images`);
      
      // Optimize image mapping
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

// Create and configure the communicator
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