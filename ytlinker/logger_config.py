import os
import logging

# Configuration constants
DEFAULT_LOG_LEVEL = logging.INFO
ENV_LOG_LEVEL = "LOG_LEVEL"

# Global dictionary to track created loggers
_loggers = {}

def setup_logger(name, level=None):
    """Set up and configure a logger instance, caching loggers in a global dictionary to avoid duplicate configuration.
    
    Args:
        name: Name of the logger
        level: Optional logging level (defaults to env var or DEFAULT_LOG_LEVEL)
        
    Returns:
        Logger instance with proper configuration
    """
    # Check if logger with this name already exists
    if name in _loggers:
        return _loggers[name]
        
    logger = logging.getLogger(name)
    
    # Get log level from environment or use default
    level_name = os.getenv(ENV_LOG_LEVEL, "").upper()
    if level_name and hasattr(logging, level_name):
        level = getattr(logging, level_name)
    else:
        level = level or DEFAULT_LOG_LEVEL
        
    logger.setLevel(level)
    
    # Save logger in the global dictionary
    _loggers[name] = logger
    return logger

# Configure the base logging configuration
def configure_logging(format_string=None, level=None):
    """Configure root logger with formatting and level
    
    Args:
        format_string: Optional custom format string
        level: Optional logging level
    """
    if format_string is None:
        format_string = '%(asctime)s - %(name)s - %(levelname)s - %(message)s'
        
    if level is None:
        # Get level from environment variable or use default
        level_name = os.getenv(ENV_LOG_LEVEL, "").upper()
        if level_name and hasattr(logging, level_name):
            level = getattr(logging, level_name)
        else:
            level = DEFAULT_LOG_LEVEL
        
    logging.basicConfig(
        format=format_string,
        level=level
    )