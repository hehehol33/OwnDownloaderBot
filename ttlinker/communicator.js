import fs from "fs";
import Logger from "./logger.js";

class WebSocketCommunicator {
  constructor() {
    this.wsClient = null;
    this.host = null;
    this.port = null;
    this.messageHandler = null;
    this.isConnected = false;
    this.logger = new Logger({ appName: 'communicator.tiktok' });
  }

  // Автоматичне визначення налаштувань на основі середовища
  autoDetectConfig() {
    // Визначення порту
    const port = process.env.PORT || 8098;
    
    // Визначення хоста на основі середовища (Docker або локальний запуск)
    const isDocker = fs.existsSync("/.dockerenv");
    const host = isDocker ? 
      (process.env.SERVER_HOST || "tgbot") : 
      (process.env.SERVER_HOST || "localhost");
    
    this.logger.info(`Connecting to ${host}:${port}`);
    
    return this.configure(host, port);
  }

  configure(host, port) {
    this.host = host;
    this.port = port;
    return this;
  }

  onMessage(handler) {
    this.messageHandler = handler;
    return this;
  }

  connect(initialMessage) {
    if (!this.host || !this.port) {
      throw new Error('Host and port must be configured before connecting');
    }

    this.wsClient = new WebSocket(`ws://${this.host}:${this.port}`);

    this.wsClient.onopen = () => {
      this.logger.info("Connected to bot");
      this.isConnected = true;
      if (initialMessage) this.send(initialMessage);
    };

    this.wsClient.onmessage = async (event) => {
      this.logger.debug("Received message from server");
      if (this.messageHandler) await this.messageHandler(event.data);
    };

    this.wsClient.onerror = (error) => {
      this.logger.error("WebSocket Error:", error.toString());
      if (this.isConnected) {
        this.send(JSON.stringify({
          error: "WebSocket Error",
          details: error.toString()
        }));
      }
    };

    this.wsClient.onclose = () => {
      this.logger.warn("Disconnected, reconnecting in 7s...");
      this.isConnected = false;
      setTimeout(() => this.connect(initialMessage), 7000);
    };

    // Коректне завершення при зупинці програми
    process.on("SIGINT", () => {
      this.logger.info("Disconnecting from bot");
      this.disconnect();
      process.exit();
    });

    return this;
  }

  send(data) {
    if (this.wsClient?.readyState === WebSocket.OPEN) {
      this.logger.debug("Sending data to server");
      this.wsClient.send(data);
    } else {
      this.logger.warn("Cannot send data - connection not open");
    }
  }

  disconnect() {
    if (this.wsClient) {
      this.wsClient.close();
      this.isConnected = false;
      this.logger.info("WebSocket connection closed");
    }
  }
}

export default WebSocketCommunicator;