// Advanced TypeScript types and patterns
export type Result<T, E = Error> = Success<T> | Failure<E>;
export type Success<T> = { success: true; data: T };
export type Failure<E> = { success: false; error: E };

// Utility types
export type Optional<T, K extends keyof T> = Omit<T, K> & Partial<Pick<T, K>>;
export type RequiredBy<T, K extends keyof T> = T & Required<Pick<T, K>>;

// Conditional types
export type NonNullable<T> = T extends null | undefined ? never : T;
export type FunctionKeys<T> = {
    [K in keyof T]: T[K] extends Function ? K : never;
}[keyof T];

// Mapped types
export type ReadonlyDeep<T> = {
    readonly [P in keyof T]: T[P] extends object ? ReadonlyDeep<T[P]> : T[P];
};

// Generic constraints
export interface Serializable {
    serialize(): string;
}

export interface Deserializable<T> {
    deserialize(data: string): T;
}

// Union types
export type ApiResponse<T> = 
    | { status: 'success'; data: T }
    | { status: 'error'; message: string; code: number }
    | { status: 'loading' };

// Intersection types
export type UserWithMetadata = User & {
    metadata: {
        lastLogin: Date;
        loginCount: number;
        preferences: Record<string, any>;
    };
};

// Template literal types
export type EventName<T extends string> = `on${Capitalize<T>}`;
export type HttpMethod = 'GET' | 'POST' | 'PUT' | 'DELETE' | 'PATCH';
export type ApiEndpoint<T extends string> = `/api/${T}`;

// Generic utility class
export class ApiClient<T extends Record<string, any>> {
    private baseUrl: string;
    private headers: Record<string, string>;
    
    constructor(baseUrl: string, headers: Record<string, string> = {}) {
        this.baseUrl = baseUrl;
        this.headers = headers;
    }
    
    async request<R>(
        endpoint: ApiEndpoint<keyof T & string>,
        method: HttpMethod = 'GET',
        data?: any
    ): Promise<Result<R>> {
        try {
            const response = await fetch(`${this.baseUrl}${endpoint}`, {
                method,
                headers: {
                    'Content-Type': 'application/json',
                    ...this.headers
                },
                body: data ? JSON.stringify(data) : undefined
            });
            
            if (!response.ok) {
                return {
                    success: false,
                    error: new Error(`HTTP ${response.status}: ${response.statusText}`)
                };
            }
            
            const result = await response.json();
            return { success: true, data: result };
        } catch (error) {
            return {
                success: false,
                error: error as Error
            };
        }
    }
    
    // Method overloading
    get<R>(endpoint: ApiEndpoint<keyof T & string>): Promise<Result<R>>;
    get<R>(endpoint: ApiEndpoint<keyof T & string>, params: Record<string, any>): Promise<Result<R>>;
    async get<R>(endpoint: ApiEndpoint<keyof T & string>, params?: Record<string, any>): Promise<Result<R>> {
        const url = params ? `${endpoint}?${new URLSearchParams(params).toString()}` : endpoint;
        return this.request<R>(url);
    }
    
    async post<R>(endpoint: ApiEndpoint<keyof T & string>, data: any): Promise<Result<R>> {
        return this.request<R>(endpoint, 'POST', data);
    }
    
    async put<R>(endpoint: ApiEndpoint<keyof T & string>, data: any): Promise<Result<R>> {
        return this.request<R>(endpoint, 'PUT', data);
    }
    
    async delete<R>(endpoint: ApiEndpoint<keyof T & string>): Promise<Result<R>> {
        return this.request<R>(endpoint, 'DELETE');
    }
}

// Complex generic with constraints
export class EventEmitter<T extends Record<string, any[]>> {
    private listeners: Partial<{ [K in keyof T]: Array<(...args: T[K]) => void> }> = {};
    
    on<K extends keyof T>(event: K, listener: (...args: T[K]) => void): void {
        if (!this.listeners[event]) {
            this.listeners[event] = [];
        }
        this.listeners[event]!.push(listener);
    }
    
    off<K extends keyof T>(event: K, listener: (...args: T[K]) => void): void {
        const eventListeners = this.listeners[event];
        if (eventListeners) {
            const index = eventListeners.indexOf(listener);
            if (index !== -1) {
                eventListeners.splice(index, 1);
            }
        }
    }
    
    emit<K extends keyof T>(event: K, ...args: T[K]): void {
        const eventListeners = this.listeners[event];
        if (eventListeners) {
            eventListeners.forEach(listener => listener(...args));
        }
    }
}

// Namespace
export namespace ValidationUtils {
    export function isEmail(value: string): boolean {
        const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
        return emailRegex.test(value);
    }
    
    export function isUUID(value: string): boolean {
        const uuidRegex = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
        return uuidRegex.test(value);
    }
    
    export function isValidPassword(password: string): boolean {
        return password.length >= 8 && 
               /[A-Z]/.test(password) && 
               /[a-z]/.test(password) && 
               /[0-9]/.test(password);
    }
    
    export class ValidationError extends Error {
        constructor(
            message: string,
            public field: string,
            public code: string
        ) {
            super(message);
            this.name = 'ValidationError';
        }
    }
}

// Re-export with type modification
export { User } from './SimpleClass';
export type ExtendedUser = User & {
    metadata: UserWithMetadata['metadata'];
    preferences: Record<string, any>;
};

// Default export
export default class TypeRegistry<T extends Record<string, any>> {
    private types: Map<string, T> = new Map();
    
    register<K extends keyof T>(name: K, type: T[K]): void {
        this.types.set(name as string, type);
    }
    
    get<K extends keyof T>(name: K): T[K] | undefined {
        return this.types.get(name as string);
    }
    
    has<K extends keyof T>(name: K): boolean {
        return this.types.has(name as string);
    }
    
    getAll(): T {
        return Object.fromEntries(this.types.entries()) as T;
    }
}