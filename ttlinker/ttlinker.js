const Tiktok = require("@tobyg74/tiktok-api-dl");
const WebSocket = require('ws');

let wsClient;

console.log('ttlinker v. A2');

function connectWebSocket() {
    wsClient = new WebSocket('ws://tgbot:8098');
    //'ws://localhost:8098'
    //'ws://tgbot:8098'

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
