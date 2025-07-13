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
from datetime import datetime, timedelta
import yt_dlp
from logger_config import setup_logger, configure_logging
from communicator import WebSocketCommunicator, MediaType, MediaItem, FetchResult

# Logger configuration
configure_logging()
logger = setup_logger("youtube.linker")

# Constants
VERSION = "A3"
MAX_RETRIES = 2
RETRY_DELAY = 1.5
MAX_WORKERS = 4
DOWNLOAD_FOLDER = r"C:\OwnDownloaderBot\testfolder"  # Папка для збереження відео
CLEANUP_INTERVAL = 60* 30  # 30 минут в секундах

# Necessary regex
RE_INITIAL_DATA = re.compile(r"ytInitialData\s*=\s*({.*?});?\s*</script>", re.DOTALL)
RE_IMAGE_QUALITY = re.compile(r"=s(\d+)-")

# Thread pool for CPU-bound operations
executor = ThreadPoolExecutor(max_workers=MAX_WORKERS)

def is_file_in_use(file_path: str) -> bool:
    """
    Проверяет, используется ли файл другим процессом.
    Возвращает True если файл заблокирован.
    """
    try:
        # Пытаемся открыть файл в режиме записи
        with open(file_path, 'r+b') as f:
            return False
    except (IOError, PermissionError):
        return True
    except Exception:
        return False

def safe_delete_file(file_path: str) -> bool:
    """
    Безопасно удаляет файл, проверяя, не используется ли он.
    Возвращает True если файл удален, False если не удалось.
    """
    try:
        if os.path.exists(file_path):
            if is_file_in_use(file_path):
                logger.warning(f"Файл {file_path} используется, пропускаем удаление")
                return False
            else:
                os.remove(file_path)
                logger.info(f"Удален файл: {file_path}")
                return True
    except Exception as e:
        logger.error(f"Ошибка при удалении файла {file_path}: {e}")
        return False
    return False

def cleanup_download_folder():
    """
    Очищает папку загрузок от старых видео файлов.
    Удаляет только файлы старше 1 часа.
    """
    try:
        if not os.path.exists(DOWNLOAD_FOLDER):
            return
            
        current_time = datetime.now()
        deleted_count = 0
        skipped_count = 0
        
        for filename in os.listdir(DOWNLOAD_FOLDER):
            if filename.endswith('.mp4'):
                file_path = os.path.join(DOWNLOAD_FOLDER, filename)
                
                # Проверяем время создания файла
                file_time = datetime.fromtimestamp(os.path.getctime(file_path))
                age_hours = (current_time - file_time).total_seconds() / 3600
                
                # Удаляем файлы старше 1 часа  . хз сколько нужно...
                if age_hours > 1:
                    if safe_delete_file(file_path):
                        deleted_count += 1
                    else:
                        skipped_count += 1
                        
        if deleted_count > 0 or skipped_count > 0:
            logger.info(f"Очистка папки: удалено {deleted_count} файлов, пропущено {skipped_count} (используются)")
            
    except Exception as e:
        logger.error(f"Ошибка при очистке папки: {e}")

async def cleanup_task():
    """
    Асинхронная задача для периодической очистки папки.
    """
    while True:
        try:
            await asyncio.sleep(CLEANUP_INTERVAL)
            logger.info("Запуск плановой очистки папки загрузок")
            cleanup_download_folder()
        except Exception as e:
            logger.error(f"Ошибка в задаче очистки: {e}")

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
        
        # Generate unique filename
        video_id = str(uuid.uuid4())[:8]
        filename = f"youtube_{video_id}.mp4"
        file_path = os.path.join(DOWNLOAD_FOLDER, filename)
        
        # Download video
        with yt_dlp.YoutubeDL({
            # Змінений формат для уникнення потреби в ffmpeg
            'format': '398/22/18/best[ext=mp4]',  # 720п потом 480 потом 360 потом их заглушка
            'quiet': True,
            'no_warnings': True,
            'noplaylist': True, 
            'outtmpl': file_path,
            'concurrent_fragment_downloads': 4,
            'cookiefile': 'cookies.txt'
        }) as ydl:
            # Сначала получим информацию о доступных форматах
            info = ydl.extract_info(url, download=False)
            
            if info:
                # Логируем доступные форматы
                formats = info.get('formats', [])
                logger.info(f"Доступные форматы для {info.get('title', 'Unknown')}:")
                for fmt in formats:
                    if fmt.get('ext') == 'mp4' and fmt.get('height'):
                        logger.info(f"  Формат {fmt.get('format_id', 'N/A')}: {fmt.get('height')}p, {fmt.get('filesize', 'N/A')} bytes")
                
                # Теперь скачиваем
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
    
    # Запускаем очистку папки параллельно с основным процессом
    cleanup_coro = cleanup_task()
    communicator_coro = communicator.run()
    
    # Запускаем обе задачи одновременно
    await asyncio.gather(cleanup_coro, communicator_coro)

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
