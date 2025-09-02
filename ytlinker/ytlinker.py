import os
import time
import json
import asyncio
import re
import requests
import uuid
import shutil
from html import unescape
from concurrent.futures import ThreadPoolExecutor
import yt_dlp
from logger_config import setup_logger, configure_logging
from communicator import WebSocketCommunicator, MediaType, MediaItem, FetchResult
from urllib.parse import urlparse, parse_qs, unquote

# Logger configuration
configure_logging()
logger = setup_logger("youtube.linker")

# Constants
VERSION = "A4"
MAX_RETRIES = 2
RETRY_DELAY = 1.5 
MAX_WORKERS = 4
DEFAULT_DOWNLOAD_FOLDER = r"C:\OwnDownloaderBot\testfolder"  # Default download folder
DOWNLOAD_FOLDER = os.getenv("DOWNLOAD_FOLDER", DEFAULT_DOWNLOAD_FOLDER)  # Download folder from environment variable, or default

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

def resolve_redirect_url(raw: str) -> str:
    if not raw:
        return raw
    try:
        if "youtube.com/redirect" in raw or raw.startswith("/redirect?"):
            if raw.startswith("/redirect?"):
                raw = "https://www.youtube.com" + raw
            p = urlparse(raw)
            qs = parse_qs(p.query)
            for key in ("q", "url", "u", "target"):
                if key in qs and qs[key]:
                    candidate = unquote(qs[key][0])
                    if candidate.startswith("http"):
                        return candidate
        return raw
    except Exception:
        return raw

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

    m = RE_INITIAL_DATA.search(response.text)
    if not m:
        logger.error("Could not find ytInitialData in page HTML")
        raise ValueError("Could not find ytInitialData in page HTML")
    initial_data = json.loads(m.group(1))

    all_image_urls: list[list[str]] = []
    post_text_runs_collected = False
    post_text_builder: list[str] = []

    def extract_from(obj):
        nonlocal post_text_runs_collected
        if isinstance(obj, dict):
            if "backstageImageRenderer" in obj:
                thumbs = obj["backstageImageRenderer"].get("image", {}).get("thumbnails", [])
                image_set = []
                for t in thumbs:
                    u = t.get("url")
                    if u:
                        image_set.append(unescape(u))
                if image_set:
                    all_image_urls.append(image_set)

            if not post_text_runs_collected:
                content_text = None
                if "backstagePostRenderer" in obj:
                    content = obj["backstagePostRenderer"].get("content", {})
                    bpcr = content.get("backstagePostContentRenderer")
                    if bpcr:
                        content_text = bpcr.get("contentText")
                elif "contentText" in obj:
                    content_text = obj.get("contentText")

                if content_text and isinstance(content_text, dict) and "runs" in content_text:
                    for run in content_text["runs"]:
                        raw_display = run.get("text", "")
                        nav = run.get("navigationEndpoint", {})
                        if "urlEndpoint" in nav and "url" in nav["urlEndpoint"]:
                            full = nav["urlEndpoint"]["url"]
                        else:
                            full = None
                        if full and full.startswith("http"):
                            resolved = resolve_redirect_url(full)
                            post_text_builder.append(resolved)
                        else:
                            post_text_builder.append(raw_display)
                    post_text_runs_collected = True

            for v in obj.values():
                extract_from(v)
        elif isinstance(obj, list):
            for item in obj:
                extract_from(item)

    extract_from(initial_data)

    best_images: list[str] = []
    for image_set in all_image_urls:
        if not image_set:
            continue
        def q(u: str) -> int:
            m2 = RE_IMAGE_QUALITY.search(u)
            return int(m2.group(1)) if m2 else 0
        best_images.append(max(image_set, key=q))

    post_text = "".join(post_text_builder).strip() if post_text_builder else None
    logger.info(f"Post contains: text={bool(post_text)}, images={len(best_images)}")
    return {"text": post_text, "images": best_images}

def _fetch_media_items_sync(url: str) -> list[MediaItem]:
    """
    Synchronous function to fetch media items from a YouTube URL.
    For videos, saves them locally and returns file:// URIs.

    Returns:
        list[MediaItem]: A list of MediaItem objects. Returns an empty list if an error occurs or no media is found.
    """
    media_items = []
    
    try:
        # Create download folder if it doesn't exist
        os.makedirs(DOWNLOAD_FOLDER, exist_ok=True)
        
        # Handle community posts
        if is_community_post(url):
            post_content = extract_post_content(url)
            
            # Add text content if available
            if post_content["text"]:
                media_items.append(MediaItem(
                    type=MediaType.TEXT,
                    content=post_content["text"]
                ))
                
            # Add all images if available
            for image_url in post_content["images"]:
                media_items.append(MediaItem(
                    type=MediaType.PHOTO,
                    url=image_url
                ))
                
            return media_items
        
        # Handle videos (both regular and shorts)
        content_type = "shorts" if is_shorts(url) else "video"
        logger.info(f"Fetching content: {url} ({content_type})")
        
        # Generate unique filename
        video_id = str(uuid.uuid4())[:8]
        filename = f"youtube_{video_id}.mp4"
        file_path = os.path.join(DOWNLOAD_FOLDER, filename)
        
        # Download video
        with yt_dlp.YoutubeDL({
            'format': '22/18/best[ext=mp4]',  #398 Is video without sound oopsie)  720p 480p existing format with sound)
            'quiet': True,
            'no_warnings': True,
            'noplaylist': True, 
            'outtmpl': file_path,
            'concurrent_fragment_downloads': 4,
            'cookiefile': 'cookies.txt'
        }) as ydl:
            # Get information about available formats
            info = ydl.extract_info(url, download=False)
            
            if info:
                # Log available formats at DEBUG level
                formats = info.get('formats', [])
                logger.debug(f"Available formats for {info.get('title', 'Unknown')}:")
                for fmt in formats:
                    if fmt.get('ext') == 'mp4' and fmt.get('height'):
                        logger.debug(f"  Format {fmt.get('format_id', 'N/A')}: {fmt.get('height')}p, filesize: {fmt.get('filesize_approx', 'N/A')} bytes")
                
                # Now download the selected format
                info = ydl.extract_info(url, download=True)
            
            if info:
                # Check if file was successfully downloaded
                if os.path.exists(file_path):
                    # Create file:// URI
                    file_uri = f"file://{file_path}"
                    
                    # Add to media items
                    media_items.append(MediaItem(
                        type=MediaType.VIDEO,
                        url=file_uri
                    ))
                    
                    # Log info
                    width = info.get('width', '?')
                    height = info.get('height', '?')
                    resolution = f"{width}x{height}"
                    video_info = f"{info.get('title', 'Unknown')} ({resolution})"
                    logger.info(f"Found {content_type}: {video_info}")
                    logger.info(f"Video saved to: {file_path}")
                    logger.info(f"File URI: {file_uri}")
                else:
                    logger.error(f"Failed to save video to {file_path}")
                
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
        # Use asyncio.run to start the main coroutine
        asyncio.run(main())
    except KeyboardInterrupt:
        logger.info("Received keyboard interrupt, shutting down")
        # Properly shutdown the executor
        executor.shutdown(wait=True)
        logger.info("Executor shutdown, exiting")
    except Exception as e:
        logger.critical(f"Unhandled exception: {e}", exc_info=True)
