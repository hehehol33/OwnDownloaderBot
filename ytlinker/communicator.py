import os
import json
import asyncio
import websockets
import time
from typing import Dict, List, Any, Optional, Callable, Awaitable
from enum import Enum
from dataclasses import dataclass
from logger_config import setup_logger, configure_logging

# Configuration constants
DEFAULT_PORT = "8098"
DEFAULT_RECONNECT_DELAY = 7
DEFAULT_HOST_DOCKER = "tgbot"
DEFAULT_HOST_LOCAL = "localhost"
ENV_PORT = "PORT"
ENV_HOST = "SERVER_HOST"

# Configure logging
configure_logging()
logger = setup_logger("communicator")

class MediaType(Enum):
    """Standard media types"""
    PHOTO = "photo"
    VIDEO = "video"
    TEXT = "text"

@dataclass
class MediaItem:
    """Media item container"""
    type: MediaType
    url: str = ""
    content: str = ""

@dataclass
class FetchResult:
    """Result container for media fetching"""
    media: List[Any]
    error: Optional[str] = None

class WebSocketCommunicator:
    """WebSocket communication handler for media downloaders"""
    
    def __init__(
        self, 
        platform_name: str,
        fetch_function: Callable[[str], Awaitable[FetchResult]],
        port: Optional[str] = None,
        host: Optional[str] = None,
        reconnect_delay: int = DEFAULT_RECONNECT_DELAY,
        log_level: Optional[int] = None
    ):
        """Initialize communicator with platform and fetch function"""
        self.platform_name = platform_name
        self.fetch_function = fetch_function
        self.reconnect_delay = reconnect_delay
        
        # Connection settings
        self.port = port or os.getenv(ENV_PORT, DEFAULT_PORT)
        default_host = DEFAULT_HOST_DOCKER if os.path.exists('/.dockerenv') else DEFAULT_HOST_LOCAL
        self.host = host or os.getenv(ENV_HOST, default_host)
        
        # Setup logger and connection
        self.logger = setup_logger(f"communicator.{platform_name}", log_level)
        self.current_websocket: Optional[websockets.WebSocketClientProtocol] = None
        
        self.logger.info(f"Using port: {self.port}, host: {self.host}")
    
    @property
    def websocket_url(self) -> str:
        """Full WebSocket URL"""
        return f"ws://{self.host}:{self.port}"
    
    async def send_media_response(self, response: Dict[str, Any]) -> None:
        """Send formatted response to client"""
        if not self.current_websocket:
            self.logger.error("No active websocket connection")
            return
        
        try:
            await self.current_websocket.send(json.dumps(response))
        except Exception as e:
            self.logger.error(f"Error sending response: {e}")
    
    async def send_error(self, message: str, details: Optional[str] = None) -> None:
        """Send error message to client"""
        if not self.current_websocket:
            self.logger.error(f"Error (not sent): {message}")
            return
            
        error_msg = {"error": message}
        if details:
            error_msg["details"] = details
            
        self.logger.error(f"Sending error: {message}")
        await self.send_media_response(error_msg)
    
    def _prepare_media_item(self, item) -> Optional[Dict[str, Any]]:
        """
        Convert media item to standard format.

        Expected item types:
        - dict with a "type" key (already formatted)
        - MediaItem dataclass or similar object with:
            - 'type' attribute (str or MediaType)
            - 'url' attribute for 'photo' or 'video'
            - 'content' or 'text' attribute for 'text'
        """
        # Already formatted dict
        if isinstance(item, dict) and "type" in item:
            return item
        
        # Handle objects with type attribute
        if hasattr(item, 'type'):
            type_value = item.type.value if hasattr(item.type, 'value') else item.type
            
            # Text content
            if type_value == 'text':
                content = getattr(item, 'content', "") or getattr(item, 'text', "")
                return {"type": "text", "content": content}
            
            # Media URLs    
            if type_value in ('photo', 'video') and hasattr(item, 'url'):
                return {"type": type_value, "url": item.url}
                
        return None
    
    async def send_result(self, result: FetchResult) -> None:
        """Process and send FetchResult"""
        if result.error:
            # If result has 'details' attribute, include it as error details
            details = getattr(result, "details", None)
            await self.send_error(result.error, details)
            return
        
        # Format media items
        media_items = []
        for item in result.media:
            if formatted_item := self._prepare_media_item(item):
                media_items.append(formatted_item)
        
        if not media_items:
            await self.send_error("No valid media found")
            return
            
        # Send response
        response = {"media": media_items}
        self.logger.info(f"Sending {len(media_items)} media items")
        await self.send_media_response(response)
    
    async def handle_link(self, websocket, url: str) -> None:
        """Process URL and send results"""
        try:
            self.current_websocket = websocket
            self.logger.info(f"Processing URL: {url}")
            result = await self.fetch_function(url)
            await self.send_result(result)
        except Exception as e:
            self.logger.exception(f"Error processing URL: {url}")
            await self.send_error("Error processing request", str(e))
    
    async def connect_websocket(self) -> None:
        """Connect to WebSocket server and handle messages"""
        self.logger.info(f"Connecting to {self.websocket_url}")
        
        async with websockets.connect(self.websocket_url) as websocket:
            self.current_websocket = websocket
            await websocket.send(f"platform:{self.platform_name}")
            self.logger.info(f"Connected as {self.platform_name} platform")

            # Message handling loop
            while True:
                url = (await websocket.recv()).strip()
                self.logger.info(f"Received link: {url}")
                await self.handle_link(websocket, url)
    
    async def run(self) -> None:
        """Main connection loop with reconnect logic"""
        while True:
            try:
                await self.connect_websocket()
            except (websockets.exceptions.ConnectionClosed, OSError):
                self.logger.warning(f"Disconnected. Reconnecting in {self.reconnect_delay}s")
                self.current_websocket = None
                await asyncio.sleep(self.reconnect_delay)
            except Exception as e:
                self.logger.exception(f"Connection error. Reconnecting in {self.reconnect_delay}s")
                self.current_websocket = None
                await asyncio.sleep(self.reconnect_delay)


