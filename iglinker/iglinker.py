import os
import time
import json
import asyncio
import instaloader
import websockets
from enum import Enum
from dataclasses import dataclass, asdict
from typing import List
#from dotenv import load_dotenv

# Load .env variables (IG_USERNAME, IG_PASSWORD, etc.)
#load_dotenv()

# Retrieve optional Instagram credentials
IG_USERNAME = os.getenv("IG_USERNAME")
IG_PASSWORD = os.getenv("IG_PASSWORD")

# File to store the logged-in session for re-use
SESSION_FILE = f"{IG_USERNAME}_session" if IG_USERNAME else "session_anonymous"

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

LOADER = instaloader.Instaloader()
LOADER.context.max_connection_attempts = 1
  

def is_docker() -> bool:
    """Check if the runtime environment is Docker by looking for a special file."""
    return os.path.exists('/.dockerenv')

# Attempt to load an existing session or log in if credentials are provided
if IG_USERNAME and IG_PASSWORD:
    try:
        # Load session if it exists
        LOADER.load_session_from_file(username=IG_USERNAME, filename=SESSION_FILE)
        print("Session loaded successfully.")
    except FileNotFoundError:
        # If no session file, try to log in and save new session
        print("No session file found, attempting to log in...")
        try:
            LOADER.login(IG_USERNAME, IG_PASSWORD)
            LOADER.save_session_to_file(filename=SESSION_FILE)
            print(f"Logged in as {IG_USERNAME}, session saved.")
        except Exception as e:
            print(f"Login failed ({e}). Continuing without credentials...")
    except Exception as e:
        print(f"Error loading session: {e}. Proceeding without credentials.")
else:
    print("No credentials found. Working anonymously.")

# Read port from environment or default to "8098"
port = os.getenv("PORT", "8098")
print(f"Using port: {port}")

default_host = "tgbot" if is_docker() else "localhost"
host = os.getenv("SERVER_HOST", default_host)
print(f"Connecting to host: {host}")
print("iglinker v. A3")

def _fetch_media_items_sync(post_url: str) -> List[MediaItem]:
    """
    Synchronous function that retrieves photo/video links from the given post URL.
    Blocks while downloading but will be run in an executor so it won't freeze the event loop.
    """
    media_items: List[MediaItem] = []

    try:
        # If the URL indicates a story...
        if "/stories/" in post_url:
            # Extract everything after the final "/" of the path
            story_id_str = post_url.rstrip("/").split("/")[-1]
            # Remove any query parameters (e.g. "?igsh=...")
            if "?" in story_id_str:
                story_id_str = story_id_str.split("?", 1)[0]

            # Convert to int so we can load that specific StoryItem
            story_id = int(story_id_str)

            # Load single story item by ID
            story_item = instaloader.StoryItem.from_mediaid(LOADER.context, story_id)
            LOADER.download_storyitem(story_item, target='.')

            media_type = MediaType.VIDEO if story_item.is_video else MediaType.PHOTO
            media_url = story_item.video_url if story_item.is_video else story_item.url
            media_items.append(MediaItem(type=media_type, url=media_url))

        else:
            # Extract the post shortcode from the URL
            post_shortcode = post_url.split('/')[-2]
            post = instaloader.Post.from_shortcode(LOADER.context, post_shortcode)

            # Check if the post is a sidecar (multiple items)
            if post.typename == "GraphSidecar":
                for node in post.get_sidecar_nodes():
                    media_type = MediaType.VIDEO if node.is_video else MediaType.PHOTO
                    media_url = node.video_url if node.is_video else node.display_url
                    media_items.append(MediaItem(type=media_type, url=media_url))
            else:
                # Single item post
                media_type = MediaType.VIDEO if post.is_video else MediaType.PHOTO
                media_url = post.video_url if post.is_video else post.url
                media_items.append(MediaItem(type=media_type, url=media_url))

    except Exception as e:
        print(f"Error fetching media items: {e}")
        # Return an empty list if an error occurs
        return []

    return media_items

async def fetch_media_items(post_url: str) -> FetchResult:
    """
    Asynchronous wrapper to execute _fetch_media_items_sync in an executor,
    so the main event loop remains responsive.
    """
    loop = asyncio.get_event_loop()
    start_time = time.time()
    media_items = await loop.run_in_executor(None, _fetch_media_items_sync, post_url)
    elapsed_time = int((time.time() - start_time) * 1000)  # Convert to milliseconds
    return FetchResult(media=media_items, time=elapsed_time)

async def send_media_items(websocket, response: FetchResult) -> None:
    """Send the collected media items over the websocket as JSON."""
    print("Found media:", response)
    await websocket.send(json.dumps(
        asdict(response),
        default=lambda obj: obj.value if isinstance(obj, MediaType) else str(obj)
    ))

async def connect_websocket() -> None:
    """
    Connect to the websocket, pass an initial greeting, then handle incoming post links.
    Automatically reconnect if the connection is lost.
    """
    websocket_url = f"ws://{host}:{port}"
    async with websockets.connect(websocket_url) as websocket:
        await websocket.send("platform:instagram")
        print("Connected to bot")

        while True:
            post_url = (await websocket.recv()).strip()
            print("Received link:", post_url)

            try:
                response = await fetch_media_items(post_url)
                await send_media_items(websocket, response)
            except instaloader.exceptions.LoginRequiredException:
                err = {"error": "Login required for this content."}
                print(err)
                await websocket.send(json.dumps(err))
            except Exception as e:
                # For other exceptions: rate limits, invalid links, etc.
                err = {"error": "Error processing request", "details": str(e)}
                print(err)
                await websocket.send(json.dumps(err))

async def main() -> None:
    """
    Main loop to keep reconnecting to the websocket if the connection is closed.
    """
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
