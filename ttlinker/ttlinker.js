const Tiktok = require("@tobyg74/tiktok-api-dl");
const WebSocket = require('ws');

let wsClient;

console.log('ttlinker v. A1'); // Тоже будет версионность 

function connectWebSocket() { 
    wsClient = new WebSocket('ws://tgbot:8098');

    wsClient.on('open', () => { 
        console.log('Connected to bot'); // Тоже перевод на англ
    });

    wsClient.on('message', async (message) => {
        const tiktok_url = message.toString();
        console.log("Received link:", tiktok_url);

        try {
            const result = await Tiktok.Downloader(tiktok_url, { // По советам ИИшки поставил более читабельный вариант вызова
                version: "v1",
                proxy: null,
                showOriginalResponse: false
            });

            const videoUrl = result.result.video?.playAddr?.[0] || result.result.video?.downloadAddr?.[0];
            //console.log(result); // Мб потом сделаешь цивилизованный дебаг-режим, а пока уберу шоб логи не захламлять

            if (videoUrl) {
                console.log("Video download link:", videoUrl);
                wsClient.send(JSON.stringify(videoUrl)); // Теперь передает ошибки на бота, шоб тот понимал, что пошло не так (и дал юзеру знать)
            } else if (Array.isArray(result.result.images)) {
                console.log("Images download link:", result.result.images);
                wsClient.send(JSON.stringify(result.result.images.slice(0, 10)));
            } else {
                console.log("Content wasn't found");
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
        console.log('Disconnected from bot, attempting to reconnect...'); // Теперь будет пробовать переподключиться, если потерял соединение 
        setTimeout(connectWebSocket, 7000);
    });
}

process.on('SIGINT', () => { // Штатно закрывает соединение при закрытии
    console.log('Disconnecting from bot');
    wsClient.close();
    process.exit();
});

connectWebSocket();
