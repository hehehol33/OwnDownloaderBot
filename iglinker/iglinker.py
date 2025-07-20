import os
import time
import asyncio
import instaloader
import random
from concurrent.futures import ThreadPoolExecutor
from functools import lru_cache
import re
from logger_config import setup_logger
from communicator import WebSocketCommunicator, MediaType, MediaItem, FetchResult

# Instagram credentials
IG_USERNAME = os.getenv("IG_USERNAME")
IG_PASSWORD = os.getenv("IG_PASSWORD")

# Constants
VERSION = "A6"  # Updated version with improved error handling
MAX_RETRIES = 3
RETRY_DELAY = 2
MAX_WORKERS = 2  # Reduced to avoid Instagram rate limits
SESSION_LIFETIME = 3600  # Reset session after 1 hour


# Setup logger for this module
logger = setup_logger("instagram.linker")

# Thread pool for parallel operations
executor = ThreadPoolExecutor(max_workers=MAX_WORKERS)

# Configure instaloader with optimized settings
LOADER = instaloader.Instaloader()
LOADER.context.max_connection_attempts = 3
LOADER.context.sleep = lambda: time.sleep(random.uniform(1.0, 3.0))  # Increased delay

# Session management
last_login_time = 0

def initialize_loader(force_new=False) -> None:
    """Initialize a fresh Instagram session."""
    global LOADER, last_login_time
    
    current_time = time.time()
    
    # Only create new session if forced or session expired
    if not force_new and last_login_time > 0 and (current_time - last_login_time) < SESSION_LIFETIME:
        logger.debug("Using existing session")
        return
        
    # Create new loader with optimized settings
    LOADER = instaloader.Instaloader()
    LOADER.context.max_connection_attempts = 3
    LOADER.context.sleep = lambda: time.sleep(random.uniform(1.0, 3.0))
    
    if not IG_USERNAME or not IG_PASSWORD:
        logger.warning("No credentials found. Working anonymously.")
        return
        
    try:
        logger.info(f"Logging in as {IG_USERNAME}...")
        LOADER.login(IG_USERNAME, IG_PASSWORD)
        last_login_time = time.time()
        logger.info("Logged in successfully.")
    except Exception as e:
        logger.error(f"Login failed: {e}")
        logger.warning("Continuing without credentials...")

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

def _fetch_media_items_sync(post_url: str) -> list[MediaItem]:
    """
    Extract media items from Instagram posts with optimized processing.
    """
    media_items: list[MediaItem] = []
    
    try:
        # Handle story URLs
        if "/stories/" in post_url:
            logger.debug(f"Processing Instagram story: {post_url}")
            story_id_str = extract_id_from_url(post_url, "story_id")
            if not story_id_str:
                story_id_str = post_url.rstrip("/").split("/")[-1].split("?")[0]
                logger.debug(f"Extracted story ID using fallback: {story_id_str}")
                
            try:
                story_id = int(story_id_str)
                logger.debug(f"Fetching story with ID: {story_id}")
                story_item = instaloader.StoryItem.from_mediaid(LOADER.context, story_id)
                
                # Don't download to disk, just get the URL
                media_type = MediaType.VIDEO if story_item.is_video else MediaType.PHOTO
                media_url = story_item.video_url if story_item.is_video else story_item.url
                logger.debug(f"Retrieved story {media_type.value}: {media_url}")
                media_items.append(MediaItem(type=media_type, url=media_url))
            except instaloader.exceptions.BadResponseException as e:
                logger.error(f"Story fetch error: {e}")
                # No retry for stories as they're ephemeral
                return []

        else:
            # Handle posts and reels
            post_shortcode = ""
            if "/p/" in post_url:
                logger.debug(f"Processing Instagram post: {post_url}")
                post_shortcode = extract_id_from_url(post_url, "post_shortcode")
            elif "/reel/" in post_url:
                logger.debug(f"Processing Instagram reel: {post_url}")
                post_shortcode = extract_id_from_url(post_url, "reel_shortcode")
            
            if not post_shortcode:
                post_shortcode = post_url.split('/')[-2]
                logger.debug(f"Extracted shortcode using fallback: {post_shortcode}")
            
            try:    
                logger.debug(f"Fetching post with shortcode: {post_shortcode}")
                post = instaloader.Post.from_shortcode(LOADER.context, post_shortcode)

                # Handle different post types with optimized collection
                if post.typename == "GraphSidecar":
                    logger.debug(f"Processing carousel post with {sum(1 for _ in post.get_sidecar_nodes())} items")
                    for node in post.get_sidecar_nodes():
                        media_type = MediaType.VIDEO if node.is_video else MediaType.PHOTO
                        media_url = node.video_url if node.is_video else node.display_url
                        logger.debug(f"Added carousel item {media_type.value}: {media_url}")
                        media_items.append(MediaItem(type=media_type, url=media_url))
                else:
                    media_type = MediaType.VIDEO if post.is_video else MediaType.PHOTO
                    media_url = post.video_url if post.is_video else post.url
                    logger.debug(f"Added single {media_type.value}: {media_url}")
                    media_items.append(MediaItem(type=media_type, url=media_url))
            except instaloader.exceptions.BadResponseException as e:
                logger.error(f"Post metadata fetch failed: {e}")
                return []

    except Exception as e:
        logger.error(f"Error fetching media items: {e}", exc_info=True)
        return []

    logger.info(f"Retrieved {len(media_items)} media items")
    return media_items


async def fetch_media_items(post_url: str) -> FetchResult:
    """
    Fetch media with optimized async handling and exponential backoff.
    """
    for attempt in range(MAX_RETRIES):
        try:
            logger.info(f"Fetching media from URL: {post_url} (attempt {attempt+1}/{MAX_RETRIES})")
            
            # Use the executor for CPU-bound operations
            media_items = await asyncio.get_running_loop().run_in_executor(
                executor, _fetch_media_items_sync, post_url
            )
            
            if not media_items and attempt < MAX_RETRIES - 1:
                retry_delay_actual = RETRY_DELAY * (2 ** attempt)
                logger.warning(f"No media items found. Retrying in {retry_delay_actual}s")
                
                # If this isn't the first attempt, try refreshing the session
                if attempt > 0:
                    logger.info("Refreshing Instagram session before retry")
                    initialize_loader(force_new=True)
                    
                await asyncio.sleep(retry_delay_actual)
                continue
                
            logger.info(f"Successfully retrieved {len(media_items)} media items")
            return FetchResult(media=media_items) 
            
        except Exception as e:
            error_message = str(e)
            logger.error(f"Error fetching media: {error_message}", exc_info=True)
            
            # Handle 403 errors with refreshed session
            if ("403 Forbidden" in error_message or "login_required" in error_message) and attempt < MAX_RETRIES - 1:
                logger.warning(f"403 Forbidden error detected. Refreshing session and retrying...")
                initialize_loader(force_new=True)
                retry_delay_actual = RETRY_DELAY * (2 ** attempt)
                await asyncio.sleep(retry_delay_actual)
                continue
                
            return FetchResult(media=[], error=error_message)
    
    # Maximum retries reached
    logger.error("Maximum retry attempts reached")
    return FetchResult(media=[], error="Maximum retry attempts reached") 

async def main() -> None:
    """Main function using WebSocketCommunicator."""
    # Initialize loader once at startup
    initialize_loader()
    
    logger.info(f"iglinker v. {VERSION} starting up")
    
    # Create WebSocketCommunicator instance
    communicator = WebSocketCommunicator(
        platform_name="instagram",
        fetch_function=fetch_media_items
    )
    
    # Run the communicator
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
