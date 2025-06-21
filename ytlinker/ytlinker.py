import os
import time
import json
import asyncio
import re
import requests
from html import unescape
from concurrent.futures import ThreadPoolExecutor
import yt_dlp
from logger_config import setup_logger, configure_logging
from communicator import WebSocketCommunicator, MediaType, MediaItem, FetchResult

# Logger configuration
configure_logging()
logger = setup_logger("youtube.linker")

# Constants
VERSION = "A2"
MAX_RETRIES = 2
RETRY_DELAY = 1.5
MAX_WORKERS = 4

# Necessary regex
RE_INITIAL_DATA = re.compile(r"ytInitialData\s*=\s*({.*?});?\s*</script>", re.DOTALL)
RE_IMAGE_QUALITY = re.compile(r"=s(\d+)-")

# Thread pool for CPU-bound operations
executor = ThreadPoolExecutor(max_workers=MAX_WORKERS)

# YouTube content type detection
def is_shorts(url: str) -> bool:
    """Determine if a URL is a YouTube Shorts video"""
    return "/shorts/" in url.lower()

def is_community_post(url: str) -> bool:
    """Determine if URL is any type of YouTube Community post"""
    return "/community" in url.lower() or "/post/" in url.lower()

def extract_post_content(post_url: str) -> dict:
    """
    Returns a dictionary with text and URL of the highest quality image from a YouTube Community post.
    """
    headers = {
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
        "Accept-Language": "en-US,en;q=0.9",
    }

    logger.info(f"Fetching content: {post_url} (community post)")
    response = requests.get(post_url, headers=headers)
    response.raise_for_status()
    
    # Extract ytInitialData JavaScript object using precompiled regex
    initial_data_match = RE_INITIAL_DATA.search(response.text)
    if not initial_data_match:
        logger.error("Could not find ytInitialData in page HTML")
        raise ValueError("Could not find ytInitialData in page HTML")
    
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
    
    # Select image with highest quality using precompiled regex
    best_image = None
    if image_urls:
        def get_quality(url):
            match = RE_IMAGE_QUALITY.search(url)
            return int(match.group(1)) if match else 0
        best_image = max(image_urls, key=get_quality)
    
    logger.info(f"Post contains: text={bool(post_text)}, image={bool(best_image)}")
    return {"text": post_text, "image": best_image}

def _fetch_media_items_sync(url: str) -> list[MediaItem]:
    """
    Synchronous function to fetch media items from a YouTube URL.

    Returns:
        list[MediaItem]: A list of MediaItem objects. Returns an empty list if an error occurs or no media is found.
    """
    media_items = []
    
    try:
        # Handle community posts
        if is_community_post(url):
            post_content = extract_post_content(url)
            
            # Add text content if available
            if post_content["text"]:
                media_items.append(MediaItem(
                    type=MediaType.TEXT,
                    content=post_content["text"]
                ))
                
            # Add image if available
            if post_content["image"]:
                media_items.append(MediaItem(
                    type=MediaType.PHOTO,
                    url=post_content["image"]
                ))
                
            return media_items
        
        # Handle videos (both regular and shorts)
        content_type = "shorts" if is_shorts(url) else "video"
        logger.info(f"Fetching content: {url} ({content_type})")
        
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
                # Get full resolution (width x height)
                width = info.get('width', '?')
                height = info.get('height', '?')
                resolution = f"{width}x{height}"
                
                # Create video info string for logging
                video_info = f"{info.get('title', 'Unknown')} ({resolution})"
                logger.info(f"Found {content_type}: {video_info}")
                
                # Create MediaItem with available information
                video_url = info.get('url')
                if video_url:
                    media_items.append(MediaItem(
                        type=MediaType.VIDEO,
                        url=video_url
                    ))
                else:
                    logger.warning("No direct video URL found in yt-dlp info")
                
    except Exception as e:
        logger.exception(f"Error fetching content: {e}")
    
    return media_items

async def fetch_media_items(url: str) -> FetchResult:
    """Asynchronous wrapper for _fetch_media_items_sync with retry logic"""
    for attempt in range(MAX_RETRIES):
        try:
            logger.info(f"Processing URL: {url} (attempt {attempt+1}/{MAX_RETRIES})")
            
            # Use thread executor for CPU-bound operations
            media_items = await asyncio.get_running_loop().run_in_executor(
                executor, _fetch_media_items_sync, url
            )
            
            if not media_items and attempt < MAX_RETRIES - 1:
                retry_delay = RETRY_DELAY * (2 ** attempt)
                logger.warning(f"No media items found. Retrying in {retry_delay}s")
                await asyncio.sleep(retry_delay)
                continue
                
            logger.info(f"Retrieved {len(media_items)} media items")
            return FetchResult(media=media_items)
            
        except Exception as e:
            error_message = str(e)
            logger.error(f"Error fetching media: {error_message}", exc_info=True)
            
            if attempt < MAX_RETRIES - 1:
                retry_delay = RETRY_DELAY * (2 ** attempt)
                logger.warning(f"Retrying in {retry_delay}s")
                await asyncio.sleep(retry_delay)
                continue
                
            return FetchResult(media=[], error=f"Failed to process YouTube URL: {error_message}")
    
    logger.error("Maximum retry attempts reached")
    return FetchResult(media=[], error="Maximum retry attempts reached")

async def main() -> None:
    """Main function using WebSocketCommunicator"""
    logger.info(f"ytlinker v. {VERSION}")
    
    communicator = WebSocketCommunicator(
        platform_name="youtube",
        fetch_function=fetch_media_items
    )
    
    logger.info("Starting WebSocket communicator")
    await communicator.run()

if __name__ == "__main__":
    try:
        # Use asyncio.run
        asyncio.run(main())
    except KeyboardInterrupt:
        logger.info("Received keyboard interrupt, shutting down")
        # Properly shutdown the executor
        executor.shutdown(wait=True)
        logger.info("Executor shutdown, exiting")
    except Exception as e:
        logger.critical(f"Unhandled exception: {e}", exc_info=True)
