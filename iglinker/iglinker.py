import instaloader
import asyncio
import websockets
import json
import time
import os

def is_docker():
    return os.path.exists('/.dockerenv')

# if not set - 8098
port = os.getenv("PORT", "8098")
print(f"Using port: {port}")

# selecting host 
if is_docker():
    host = os.getenv("SERVER_HOST", "tgbot") # if in docker image
else:
    host = os.getenv("SERVER_HOST", "localhost") # if not
print(f"Connecting to host: {host}")

print("iglinker v. A2")

async def connect_websocket():
    websocket_url = f"ws://{host}:{port}"
    async with websockets.connect(websocket_url) as websocket:
        await websocket.send("platform:instagram")
        print("Connected to bot")

        while True:
            message = await websocket.recv()
            post_url = message.strip()
            print("Received link:", post_url)
            loader = instaloader.Instaloader()
            start_time = time.time()

            try:
                media_items = []
                if "/stories/" in post_url:
                    username = post_url.split('/stories/')[1].split('/')[0]
                    loader.download_stories([username])
                    media_items.append({"type": "photo", "url": f"Stories from @{username} have been downloaded."})
                else:
                    post = instaloader.Post.from_shortcode(loader.context, post_url.split('/')[-2])
                    if post.typename == "GraphSidecar":
                        for node in post.get_sidecar_nodes():
                            media_type = 'video' if node.is_video else 'photo'
                            media_url = node.video_url if node.is_video else node.display_url
                            media_items.append({'type': media_type, 'url': media_url})
                    else:
                        media_type = 'video' if post.is_video else 'photo'
                        media_url = post.video_url if post.is_video else post.url
                        media_items.append({'type': media_type, 'url': media_url})

                response = {"media": media_items, "time": round(time.time() - start_time, 2)}
                print("Found media:", response)
                await websocket.send(json.dumps(response))

            except instaloader.exceptions.LoginRequiredException:
                error_message = {"error": "Login required for this content."}
                print(error_message)
                await websocket.send(json.dumps(error_message))
            except Exception as e:
                error_message = {"error": "Error processing request", "details": str(e)}
                print(error_message)
                await websocket.send(json.dumps(error_message))

async def main():
    while True:
        try:
            await connect_websocket()
        except websockets.exceptions.ConnectionClosed:
            print("Disconnected. Reconnecting in 7 seconds...")
            await asyncio.sleep(7)

if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("Disconnecting from bot")
