const Tiktok = require("@tobyg74/tiktok-api-dl");
const WebSocket = require('ws');

// Function to check if the program is running in Docker
function isDocker() {
    const fs = require('fs');
    return fs.existsSync('/.dockerenv');
}

console.log('ttlinker v. A3');

// Get the port from the environment variable or use the default
const port = process.env.PORT || 8098;
console.log(`Using port: ${port}`);

// Set the host depending on the environment (Docker or not)
const host = isDocker() ? process.env.SERVER_HOST || 'tgbot' : process.env.SERVER_HOST || 'localhost';
console.log(`Connecting to host: ${host}`);

let wsClient;

function connectWebSocket() {
    wsClient = new WebSocket(`ws://${host}:${port}`);

    wsClient.on('open', () => {
        console.log('Connected to bot');
        wsClient.send('platform:tiktok'); // Send registration message
    });

    wsClient.on('message', async (message) => {
        const tiktok_url = message.toString();
        console.log("Received link:", tiktok_url);

        try {
            const result = await Tiktok.Downloader(tiktok_url, {
                version: "v1",
                proxy: null,
                showOriginalResponse: false
            });

            const videoUrl = result.result.video?.playAddr?.[0] || result.result.video?.downloadAddr?.[0];

            if (videoUrl) {
                console.log("Video download link:", videoUrl);
                wsClient.send(JSON.stringify({
                    media: [{ type: "video", url: videoUrl }]
                }));
            } else if (Array.isArray(result.result.images)) {
                console.log("Images download link:", result.result.images);
                const mediaArray = result.result.images.slice(0, 10).map(url => ({ type: "photo", url }));
                wsClient.send(JSON.stringify({
                    media: mediaArray
                }));
            } else {
                console.log("Content wasn't found");
                wsClient.send(JSON.stringify({ error: "Content wasn't found" }));
            }
        } catch (error) {
            console.error("Error processing request:", error);
            wsClient.send(JSON.stringify({ error: "Error processing request", details: error.message }));
        }
    });

    wsClient.on('error', (error) => {
        console.error('WebSocket Error:', error);
        wsClient.send(JSON.stringify({ error: "WebSocket Error", details: error.message }));
    });

    wsClient.on('close', () => {
        console.log('Disconnected from bot, attempting to reconnect...');
        setTimeout(connectWebSocket, 7000);
    });
}

process.on('SIGINT', () => {
    console.log('Disconnecting from bot');
    wsClient.close();
    process.exit();
});

connectWebSocket();
