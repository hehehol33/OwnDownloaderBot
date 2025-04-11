import fs from "fs"; 

// Function to check if the program is running in Docker
function isDocker() {
  return fs.existsSync("/.dockerenv");
}

console.log("ttlinker v. A4");

// Get the port from the environment variable or use the default
const port = process.env.PORT || 8098;
console.log(`Using port: ${port}`);

// Set the host depending on the environment (Docker or not)
const host = isDocker() ? process.env.SERVER_HOST || "tgbot" : process.env.SERVER_HOST || "localhost";
console.log(`Connecting to host: ${host}`);

// TikWM fetcher
async function fetchTikwmData(tiktokUrl) {
  const response = await fetch(`https://tikwm.com/api?url=${encodeURIComponent(tiktokUrl)}`);
  // Quick check before parsing JSON (reduces unneeded parsing on error)
  if (!response.ok) throw new Error(`API Request failed with status ${response.status}`);
  const { data } = await response.json();
  return data;
}

let wsClient;

function connectWebSocket() {
  wsClient = new WebSocket(`ws://${host}:${port}`);

  wsClient.addEventListener("open", () => {
    console.log("Connected to bot");
    wsClient.send("platform:tiktok");
  });

  wsClient.addEventListener("message", async (event) => {
    const tiktok_url = event.data.toString();
    console.log("Received link:", tiktok_url);

    try {
      const result = await fetchTikwmData(tiktok_url);

      if (Array.isArray(result?.images) && result.images.length > 0) { 
        console.log("Images download link:", result.images);
        const mediaArray = result.images.slice(0, 10).map((url) => ({
          type: "photo",
          url,
        }));
        wsClient.send(JSON.stringify({ media: mediaArray }));
      } else if (result?.play) {
        console.log("Video download link:", result.play);
        wsClient.send(JSON.stringify({
          media: [{ type: "video", url: result.play }],
        }));
      } else {
        console.log("Content wasn't found");
        wsClient.send(JSON.stringify({ error: "Content wasn't found" }));
      }
    } catch (error) {
      console.error("Error processing request:", error);
      wsClient.send(JSON.stringify({
        error: "Error processing request",
        details: error.message,
      }));
    }
  });

  wsClient.addEventListener("error", (error) => {
    console.error("WebSocket Error:", error);
    if (wsClient.readyState === wsClient.OPEN) {
      wsClient.send(JSON.stringify({
        error: "WebSocket Error",
        details: error.message,
      }));
    }
  });

  // Reconnect on close
  wsClient.addEventListener("close", () => {
    console.log("Disconnected from bot, attempting to reconnect...");
    setTimeout(connectWebSocket, 7000);
  });
}

// Graceful shutdown 
process.on("SIGINT", () => {
  console.log("Disconnecting from bot");
  wsClient.close();
  process.exit();
});

// Initial connect
connectWebSocket();