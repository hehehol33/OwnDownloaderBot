import os
import time
import json
import asyncio
import websockets
import yt_dlp
import re
import requests
from html import unescape
from enum import Enum
from dataclasses import dataclass, asdict
from typing import List, Optional, Dict, Any, Union

print("ytlinker A1")

# Media type definitions - updated for photo type
class MediaType(Enum):
    VIDEO = "video"
    SHORTS = "shorts"
    COMMUNITY = "community"
    IMAGE = "image"  # For community post images
    PHOTO = "photo"  # The type to send over websocket for images

@dataclass
class MediaItem:
    type: MediaType
    url: str
    resolution: str = ""
    title: str = ""
    text: str = ""  # For community post text content

@dataclass
class FetchResult:
    media: List[MediaItem]
    time: int  # Time in milliseconds

def is_docker() -> bool:
    """Check if running in Docker environment"""
    return os.path.exists('/.dockerenv')

# Network configuration
port = os.getenv("PORT", "8098")
print(f"Using port: {port}")

default_host = "tgbot" if is_docker() else "localhost"
host = os.getenv("SERVER_HOST", default_host)
print(f"Connecting to host: {host}")

def is_shorts(url: str) -> bool:
    """Determine if a URL is a YouTube Shorts video"""
    return "/shorts/" in url.lower()

def is_community_post(url: str) -> bool:
    """Determine if a URL is a YouTube Community post"""
    return "/community" in url.lower()

def is_post(url: str) -> bool:
    """Determine if a URL is a YouTube post URL"""
    return "/post/" in url.lower()

def extract_post_content(post_url: str) -> Dict[str, Any]:
    """
    Returns a dictionary with text and URL of the highest quality image from a YouTube Community post.
    """
    headers = {
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
        "Accept-Language": "en-US,en;q=0.9",
    }

    response = requests.get(post_url, headers=headers)
    response.raise_for_status()
    
    # Extract ytInitialData JavaScript object
    initial_data_match = re.search(r"ytInitialData\s*=\s*({.*?});</script>", response.text, re.DOTALL)
    if not initial_data_match:
        raise ValueError("Could not find ytInitialData in page HTML.")
    
    initial_data = json.loads(initial_data_match.group(1))

    # Recursively search for data
    image_urls = []
    post_text = None

    def extract_from_dict(obj):
        nonlocal post_text
        if not isinstance(obj, (dict, list)):
            return
            
        if isinstance(obj, dict):
            # Look for images
            if "backstageImageRenderer" in obj:
                for thumb in obj["backstageImageRenderer"].get("image", {}).get("thumbnails", []):
                    if url := thumb.get("url"):
                        image_urls.append(unescape(url))
            
            # Look for post text
            content_text = None
            if "backstagePostRenderer" in obj:
                content = obj["backstagePostRenderer"].get("content", {})
                if "backstagePostContentRenderer" in content:
                    content_text = content["backstagePostContentRenderer"].get("contentText", {})
            elif "contentText" in obj:
                content_text = obj["contentText"]
                
            if content_text and "runs" in content_text and not post_text:
                post_text = "".join(run.get("text", "") for run in content_text["runs"])
                
            # Recursive traversal
            for value in obj.values():
                extract_from_dict(value)
        else:  # list
            for item in obj:
                extract_from_dict(item)

    extract_from_dict(initial_data)
    
    # Select image with highest quality
    best_image = None
    if image_urls:
        best_image = max(image_urls, key=lambda url: int(re.search(r"=s(\d+)-", url).group(1)) 
                                     if re.search(r"=s(\d+)-", url) else 0)
    
    return {"text": post_text, "image": best_image}

def _fetch_media_items_sync(url: str) -> List[MediaItem]:
    """Synchronous function to fetch media items from YouTube URL"""
    media_items = []
    
    try:
        # Check if it's a community post or a post URL
        if is_community_post(url) or is_post(url):
            post_content = extract_post_content(url)
            
            # Add text as a community type item
            if post_content["text"]:
                media_items.append(MediaItem(
                    type=MediaType.COMMUNITY,
                    url=url,  # Original URL
                    title="Community Post",
                    text=post_content["text"]
                ))
                
            # Add image if available
            if post_content["image"]:
                media_items.append(MediaItem(
                    type=MediaType.IMAGE,
                    url=post_content["image"],
                    resolution="high",  # We're already getting the best quality
                    title="Community Post Image"
                ))
                
            return media_items
        
        # Existing video/shorts handling
        media_type = MediaType.SHORTS if is_shorts(url) else MediaType.VIDEO
        
        # Configure yt-dlp options
        ydl_opts = {
            'format': 'best[ext=mp4]',
            'quiet': True,
            'no_warnings': True,
            'noplaylist': True,
        }
        
        # Extract information without downloading
        with yt_dlp.YoutubeDL(ydl_opts) as ydl:
            info = ydl.extract_info(url, download=False)
            
            if info:
                # Create MediaItem with available information
                media_items.append(MediaItem(
                    type=media_type,
                    url=info.get('url'),
                    resolution=f"{info.get('height', 'Unknown')}p",
                    title=info.get('title', 'Untitled')
                ))
                
    except Exception as e:
        print(f"Error fetching content: {e}")
    
    return media_items

async def fetch_media_items(url: str) -> FetchResult:
    """Asynchronous wrapper for _fetch_media_items_sync"""
    loop = asyncio.get_event_loop()
    start_time = time.time()
    media_items = await loop.run_in_executor(None, _fetch_media_items_sync, url)
    elapsed_time = int((time.time() - start_time) * 1000)  # Convert to milliseconds
    return FetchResult(media=media_items, time=elapsed_time)

async def send_media_items(websocket, response: FetchResult) -> None:
    """Send media items response through websocket"""
    print(f"Found {len(response.media)} media items (took {response.time}ms)")
    
    # Debug: Print all media items received
    for item in response.media:
        print(f"Processing media item: type={item.type.value}, url={item.url}")

    # Prepare the output JSON
    output = {"media": []}

    # Add photo types first
    for item in response.media:
        if item.type == MediaType.IMAGE:
            # Convert image to photo type
            output["media"].append({
                "type": "photo",
                "url": item.url
            })

    # Add videos and shorts
    for item in response.media:
        if item.type == MediaType.VIDEO or item.type == MediaType.SHORTS:
            output["media"].append({
                "type": "video",
                "url": item.url,
                "title": item.title
            })

    # Add text types after photo
    for item in response.media:
        if item.type == MediaType.COMMUNITY:
            # Add text as a separate type
            output["media"].append({
                "type": "text",
                "content": item.text
            })

    # Debug: Print the output being sent
    print(f"Sending response: {json.dumps(output)}")

    # Send the modified response
    await websocket.send(json.dumps(output))

async def connect_websocket() -> None:
    """Connect to WebSocket server and handle messages"""
    websocket_url = f"ws://{host}:{port}"
    async with websockets.connect(websocket_url) as websocket:
        await websocket.send("platform:youtube")
        print("Connected to bot")

        while True:
            url = (await websocket.recv()).strip()
            print("Received link:", url)

            # Validate URL
            if not url.startswith("http://") and not url.startswith("https://"):
                print(f"Ignored invalid URL: {url}")
                continue  # Skip invalid URLs without sending an error

            try:
                response = await fetch_media_items(url)
                
                if not response.media:
                    error_msg = {"error": "No media found", "details": "Could not extract any media from the provided URL"}
                    print(error_msg)
                    await websocket.send(json.dumps(error_msg))
                else:
                    await send_media_items(websocket, response)
                    
            except Exception as e:
                error_msg = {"error": "Error processing request", "details": str(e)}
                print(f"Error: {error_msg}")
                await websocket.send(json.dumps(error_msg))

async def main() -> None:
    """Main application loop with reconnect logic"""
    while True:
        try:
            await connect_websocket()
        except websockets.exceptions.ConnectionClosed:
            print("Disconnected. Reconnecting in 7 seconds...")
            await asyncio.sleep(7)
        except Exception as e:
            print(f"Connection error: {e}. Reconnecting in 7 seconds...")
            await asyncio.sleep(7)

if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("Disconnecting from bot")
