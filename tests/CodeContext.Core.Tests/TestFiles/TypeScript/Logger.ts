// Logger interface and implementation
export interface ILogger {
    log(message: string, level?: LogLevel): void;
    error(message: string, error?: Error): void;
    warn(message: string): void;
    info(message: string): void;
    debug(message: string): void;
}

export enum LogLevel {
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

export class Logger implements ILogger {
    private minLevel: LogLevel;
    private prefix: string;
    
    constructor(prefix: string = '', minLevel: LogLevel = LogLevel.Info) {
        this.prefix = prefix;
        this.minLevel = minLevel;
    }
    
    log(message: string, level: LogLevel = LogLevel.Info): void {
        if (level >= this.minLevel) {
            const timestamp = new Date().toISOString();
            const levelStr = LogLevel[level].toUpperCase();
            const fullMessage = `[${timestamp}] ${levelStr} ${this.prefix}: ${message}`;
            console.log(fullMessage);
        }
    }
    
    error(message: string, error?: Error): void {
        this.log(`${message}${error ? ` - ${error.stack}` : ''}`, LogLevel.Error);
    }
    
    warn(message: string): void {
        this.log(message, LogLevel.Warn);
    }
    
    info(message: string): void {
        this.log(message, LogLevel.Info);
    }
    
    debug(message: string): void {
        this.log(message, LogLevel.Debug);
    }
    
    static createDefault(): Logger {
        return new Logger('APP');
    }
}

// Decorator for logging method calls
export function LogMethodCall(target: any, propertyKey: string, descriptor: PropertyDescriptor) {
    const originalMethod = descriptor.value;
    
    descriptor.value = function(...args: any[]) {
        const logger = this.logger || Logger.createDefault();
        logger.debug(`Calling ${propertyKey} with args: ${JSON.stringify(args)}`);
        
        try {
            const result = originalMethod.apply(this, args);
            logger.debug(`${propertyKey} completed successfully`);
            return result;
        } catch (error) {
            logger.error(`${propertyKey} failed`, error);
            throw error;
        }
    };
    
    return descriptor;
}