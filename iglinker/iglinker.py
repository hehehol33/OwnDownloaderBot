import os
import time
import json
import asyncio
import instaloader
import websockets
import random
from enum import Enum
from dataclasses import dataclass, asdict, field
from typing import List, Optional, Dict, Any, TypeAlias
from concurrent.futures import ThreadPoolExecutor
from functools import lru_cache
import re

# Retrieve optional Instagram credentials
IG_USERNAME = os.getenv("IG_USERNAME")
IG_PASSWORD = os.getenv("IG_PASSWORD")

# Constants
DEFAULT_PORT = "8098"
VERSION = "A4"
# Instagram API settings
MAX_RETRIES = 2
RETRY_DELAY = 1
MAX_WORKERS = 4  # Limiting to avoid Instagram rate limits

# Thread pool for parallel operations
executor = ThreadPoolExecutor(max_workers=MAX_WORKERS)

class MediaType(Enum):
    PHOTO = "photo"
    VIDEO = "video"

@dataclass
class MediaItem:
    type: MediaType
    url: str

@dataclass
class FetchResult:
    media: List[MediaItem]
    time: int  # Time in milliseconds
    error: Optional[str] = None

# Type hints using Python 3.12 syntax
JSON: TypeAlias = Dict[str, Any]

# Configure instaloader with optimized settings
LOADER = instaloader.Instaloader()
LOADER.context.max_connection_attempts = 3
LOADER.context.sleep = lambda: time.sleep(random.uniform(0.5, 1.5))  # Slightly reduced delay

def is_docker() -> bool:
    """Check if the runtime environment is Docker using a more efficient approach."""
    return os.path.exists('/.dockerenv')

def initialize_loader() -> None:
    """Initialize a fresh Instagram session."""
    global LOADER
    
    # Create new loader with optimized settings
    LOADER = instaloader.Instaloader()
    LOADER.context.max_connection_attempts = 3
    LOADER.context.sleep = lambda: time.sleep(random.uniform(0.5, 1.5))
    
    if not IG_USERNAME or not IG_PASSWORD:
        print("No credentials found. Working anonymously.")
        return
        
    try:
        print(f"Logging in as {IG_USERNAME}...")
        LOADER.login(IG_USERNAME, IG_PASSWORD)
        print(f"Logged in successfully.")
    except Exception as e:
        print(f"Login failed: {e}. Continuing without credentials...")

# Extract URL patterns once at module level
URL_PATTERNS = {
    "story_id": re.compile(r"/stories/(?:[^/]+)/([^/?]+)"),
    "post_shortcode": re.compile(r"/p/([^/?]+)"),
    "reel_shortcode": re.compile(r"/reel/([^/?]+)")
}

@lru_cache(maxsize=32)
def extract_id_from_url(post_url: str, pattern_key: str) -> str:
    """Extract IDs from Instagram URLs using regex (cached for performance)."""
    match = URL_PATTERNS[pattern_key].search(post_url)
    return match.group(1) if match else ""

def _fetch_media_items_sync(post_url: str) -> List[MediaItem]:
    """
    Extract media items from Instagram posts with optimized processing.
    """
    media_items: List[MediaItem] = []

    try:
        # Handle story URLs
        if "/stories/" in post_url:
            story_id_str = extract_id_from_url(post_url, "story_id")
            if not story_id_str:
                story_id_str = post_url.rstrip("/").split("/")[-1].split("?")[0]
                
            story_id = int(story_id_str)
            story_item = instaloader.StoryItem.from_mediaid(LOADER.context, story_id)
            
            # Don't download to disk, just get the URL
            media_type = MediaType.VIDEO if story_item.is_video else MediaType.PHOTO
            media_url = story_item.video_url if story_item.is_video else story_item.url
            media_items.append(MediaItem(type=media_type, url=media_url))

        else:
            # Handle posts and reels
            post_shortcode = ""
            if "/p/" in post_url:
                post_shortcode = extract_id_from_url(post_url, "post_shortcode")
            elif "/reel/" in post_url:
                post_shortcode = extract_id_from_url(post_url, "reel_shortcode")
            
            if not post_shortcode:
                post_shortcode = post_url.split('/')[-2]
                
            post = instaloader.Post.from_shortcode(LOADER.context, post_shortcode)

            # Handle different post types with optimized collection
            if post.typename == "GraphSidecar":
                for node in post.get_sidecar_nodes():
                    media_type = MediaType.VIDEO if node.is_video else MediaType.PHOTO
                    media_url = node.video_url if node.is_video else node.display_url
                    media_items.append(MediaItem(type=media_type, url=media_url))
            else:
                media_type = MediaType.VIDEO if post.is_video else MediaType.PHOTO
                media_url = post.video_url if post.is_video else post.url
                media_items.append(MediaItem(type=media_type, url=media_url))

    except Exception as e:
        print(f"Error fetching media items: {e}")
        return []

    return media_items

async def fetch_media_items(post_url: str) -> FetchResult:
    """
    Fetch media with optimized async handling and exponential backoff.
    """
    start_time = time.time()
    
    for attempt in range(MAX_RETRIES):
        try:
            # Use the executor for CPU-bound operations
            media_items = await asyncio.get_running_loop().run_in_executor(
                executor, _fetch_media_items_sync, post_url
            )
            
            elapsed_time = int((time.time() - start_time) * 1000)
            
            if not media_items and attempt < MAX_RETRIES - 1:
                retry_delay_actual = RETRY_DELAY * (2 ** attempt)
                print(f"No media items found. Retrying in {retry_delay_actual}s (attempt {attempt + 1}/{MAX_RETRIES})")
                await asyncio.sleep(retry_delay_actual)
                continue
                
            return FetchResult(media=media_items, time=elapsed_time)
            
        except Exception as e:
            error_message = str(e)
            elapsed_time = int((time.time() - start_time) * 1000)
            
            # Handle 403 errors with refreshed session
            if "403 Forbidden" in error_message and IG_USERNAME and IG_PASSWORD and attempt < MAX_RETRIES - 1:
                print(f"403 Forbidden error detected. Refreshing session and retrying...")
                initialize_loader()
                retry_delay_actual = RETRY_DELAY * (2 ** attempt)
                await asyncio.sleep(retry_delay_actual)
                continue
                
            return FetchResult(media=[], time=elapsed_time, error=error_message)
    
    # Maximum retries reached
    elapsed_time = int((time.time() - start_time) * 1000)
    return FetchResult(media=[], time=elapsed_time, error="Maximum retry attempts reached")

async def send_media_items(websocket, response: FetchResult) -> None:
    """Send media items over websocket with optimized JSON serialization."""
    print("Found media:", response)
    
    # Create response dictionary more efficiently
    response_dict = asdict(response)
    if response_dict["error"] is None:
        response_dict.pop("error")
    
    # Use a faster JSON serialization approach
    await websocket.send(json.dumps(
        response_dict,
        default=lambda obj: obj.value if isinstance(obj, MediaType) else str(obj)
    ))

async def handle_link(websocket, post_url: str) -> None:
    """Handle a single Instagram link."""
    try:
        response = await fetch_media_items(post_url)
        await send_media_items(websocket, response)
    except instaloader.exceptions.LoginRequiredException:
        err = {"error": "Login required for this content."}
        print(err)
        await websocket.send(json.dumps(err))
    except Exception as e:
        err = {"error": "Error processing request", "details": str(e)}
        print(err)
        await websocket.send(json.dumps(err))

async def connect_websocket() -> None:
    """Connect to websocket and handle incoming links."""
    host = os.getenv("SERVER_HOST", "tgbot" if is_docker() else "localhost")
    port = os.getenv("PORT", DEFAULT_PORT)
    websocket_url = f"ws://{host}:{port}"
    
    async with websockets.connect(websocket_url) as websocket:
        await websocket.send("platform:instagram")
        print("Connected to bot")

        while True:
            post_url = (await websocket.recv()).strip()
            print(f"Received link: {post_url}")
            # Process each link in the same task
            await handle_link(websocket, post_url)

async def main() -> None:
    """Main function with improved error handling."""
    # Initialize loader once at startup
    initialize_loader()
    
    # Read port and host settings
    port = os.getenv("PORT", DEFAULT_PORT)
    host = os.getenv("SERVER_HOST", "tgbot" if is_docker() else "localhost")
    
    print(f"Using port: {port}")
    print(f"Connecting to host: {host}")
    print(f"iglinker v. {VERSION}")
    
    # Use task groups for cleaner async management (Python 3.11+)
    while True:
        try:
            await connect_websocket()
        except websockets.exceptions.ConnectionClosed:
            print("Disconnected. Reconnecting in 5 seconds...")
            await asyncio.sleep(5)
        except Exception as e:
            print(f"Connection error: {e}. Reconnecting in 10 seconds...")
            await asyncio.sleep(10)

if __name__ == "__main__":
    try:
        # Use asyncio.run with improved Python 3.11+ behavior
        asyncio.run(main())
    except KeyboardInterrupt:
        print("Disconnecting from bot")
        # Properly shutdown the executor
        executor.shutdown(wait=False)
