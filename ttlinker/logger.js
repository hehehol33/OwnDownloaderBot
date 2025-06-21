/**
 * Логер, сумісний з форматом Python-логера в проекті
 */
class Logger {
  static LEVELS = {
    DEBUG: 0,
    INFO: 1,
    WARN: 2,
    ERROR: 3,
    NONE: 4
  };

  constructor(options = {}) {
    // Отримання рівня логування з env або використання значення за замовчуванням
    const envLogLevel = process.env.LOG_LEVEL?.toUpperCase();
    let configLevel = Logger.LEVELS.DEBUG;
    
    if (envLogLevel && Logger.LEVELS[envLogLevel] !== undefined) {
      configLevel = Logger.LEVELS[envLogLevel];
    }
    
    this.level = options.level !== undefined ? options.level : configLevel;
    this.useColors = options.useColors !== false;
    this.appName = options.appName || 'App';
  }

  formatMessage(level, message) {
    // Формат: "YYYY-MM-DD HH:MM:SS.mmm - appName - LEVEL - message"
    const timestamp = new Date().toISOString().replace('T', ' ').replace('Z', '');
    return `${timestamp} - ${this.appName} - ${level} - ${message}`;
  }

  debug(...args) {
    if (this.level <= Logger.LEVELS.DEBUG) {
      const message = this.formatMessage('DEBUG', args.join(' '));
      console.debug(this.useColors ? `\x1b[36m${message}\x1b[0m` : message);
    }
  }

  info(...args) {
    if (this.level <= Logger.LEVELS.INFO) {
      const message = this.formatMessage('INFO', args.join(' '));
      console.info(this.useColors ? `\x1b[32m${message}\x1b[0m` : message);
    }
  }

  warn(...args) {
    if (this.level <= Logger.LEVELS.WARN) {
      const message = this.formatMessage('WARN', args.join(' '));
      console.warn(this.useColors ? `\x1b[33m${message}\x1b[0m` : message);
    }
  }

  error(...args) {
    if (this.level <= Logger.LEVELS.ERROR) {
      const message = this.formatMessage('ERROR', args.join(' '));
      console.error(this.useColors ? `\x1b[31m${message}\x1b[0m` : message);
    }
  }

  // Логування об'єктів JSON у гарному форматі
  logObject(level, prefix, obj) {
    const jsonString = JSON.stringify(obj, null, 2);
    this[level](`${prefix}:\n${jsonString}`);
  }

  // Статичний метод для створення логера, подібно до Python-версії
  static setup(name, level) {
    return new Logger({ appName: name, level });
  }
}

// Налаштування базового логера для узгодження з Python-версією
export function configure_logging() {
  // Встановлюємо рівень логування з env
  const envLogLevel = process.env.LOG_LEVEL?.toUpperCase();
  if (envLogLevel && console[envLogLevel.toLowerCase()]) {
    console.log(`Setting log level to ${envLogLevel}`);
  }
}

export default Logger;