import { Result } from './AdvancedTypes';
import { Logger } from './Logger';

// Async/await patterns
export class AsyncUserService {
    private logger: Logger;
    private cache: Map<string, any> = new Map();
    
    constructor(logger: Logger) {
        this.logger = logger;
    }
    
    // Basic async method
    async fetchUser(id: string): Promise<User | null> {
        this.logger.info(`Fetching user: ${id}`);
        
        // Simulate async operation
        await this.delay(100);
        
        // Check cache first
        if (this.cache.has(id)) {
            this.logger.info(`User found in cache: ${id}`);
            return this.cache.get(id);
        }
        
        // Simulate API call
        const user = await this.makeApiCall<User>(`/users/${id}`);
        if (user) {
            this.cache.set(id, user);
        }
        
        return user;
    }
    
    // Async with error handling
    async createUser(userData: CreateUserData): Promise<Result<User>> {
        try {
            this.logger.info('Creating new user');
            
            // Validate data
            await this.validateUserData(userData);
            
            // Create user
            const user = await this.makeApiCall<User>('/users', 'POST', userData);
            
            if (user) {
                this.cache.set(user.id, user);
                this.logger.info(`User created successfully: ${user.id}`);
                return { success: true, data: user };
            }
            
            return { success: false, error: new Error('Failed to create user') };
        } catch (error) {
            this.logger.error('Error creating user', error as Error);
            return { success: false, error: error as Error };
        }
    }
    
    // Async generator
    async* getUsersStream(): AsyncGenerator<User, void, unknown> {
        this.logger.info('Starting user stream');
        
        let page = 1;
        const pageSize = 10;
        
        while (true) {
            const users = await this.makeApiCall<User[]>(`/users?page=${page}&size=${pageSize}`);
            if (!users || users.length === 0) {
                break;
            }
            
            for (const user of users) {
                yield user;
            }
            
            page++;
            await this.delay(50); // Rate limiting
        }
        
        this.logger.info('User stream completed');
    }
    
    // Promise.all pattern
    async fetchMultipleUsers(ids: string[]): Promise<(User | null)[]> {
        this.logger.info(`Fetching ${ids.length} users`);
        
        const promises = ids.map(id => this.fetchUser(id));
        const results = await Promise.all(promises);
        
        const successCount = results.filter(r => r !== null).length;
        this.logger.info(`Successfully fetched ${successCount}/${ids.length} users`);
        
        return results;
    }
    
    // Promise.allSettled pattern
    async fetchUsersWithErrors(ids: string[]): Promise<UserFetchResult[]> {
        this.logger.info(`Fetching ${ids.length} users (with error handling)`);
        
        const promises = ids.map(async (id): Promise<UserFetchResult> => {
            try {
                const user = await this.fetchUser(id);
                return { id, success: true, user };
            } catch (error) {
                return { id, success: false, error: error as Error };
            }
        });
        
        const results = await Promise.allSettled(promises);
        
        return results.map(result => 
            result.status === 'fulfilled' ? result.value : 
            { id: '', success: false, error: new Error('Promise rejected') }
        );
    }
    
    // Async with timeout
    async fetchUserWithTimeout(id: string, timeoutMs: number = 5000): Promise<User | null> {
        const timeoutPromise = new Promise<never>((_, reject) => {
            setTimeout(() => reject(new Error('Request timeout')), timeoutMs);
        });
        
        try {
            const user = await Promise.race([
                this.fetchUser(id),
                timeoutPromise
            ]);
            
            return user;
        } catch (error) {
            this.logger.error(`Timeout fetching user ${id}`, error as Error);
            return null;
        }
    }
    
    // Async with retry
    async fetchUserWithRetry(id: string, maxRetries: number = 3): Promise<User | null> {
        let lastError: Error | null = null;
        
        for (let attempt = 1; attempt <= maxRetries; attempt++) {
            try {
                this.logger.info(`Fetching user ${id} (attempt ${attempt}/${maxRetries})`);
                const user = await this.fetchUser(id);
                return user;
            } catch (error) {
                lastError = error as Error;
                this.logger.warn(`Attempt ${attempt} failed: ${error}`);
                
                if (attempt < maxRetries) {
                    await this.delay(Math.pow(2, attempt) * 1000); // Exponential backoff
                }
            }
        }
        
        this.logger.error(`All ${maxRetries} attempts failed for user ${id}`, lastError!);
        return null;
    }
    
    // Async iteration
    async processUsersInBatches(userIds: string[], batchSize: number = 5): Promise<void> {
        this.logger.info(`Processing ${userIds.length} users in batches of ${batchSize}`);
        
        for (let i = 0; i < userIds.length; i += batchSize) {
            const batch = userIds.slice(i, i + batchSize);
            this.logger.info(`Processing batch ${Math.floor(i / batchSize) + 1}`);
            
            const users = await this.fetchMultipleUsers(batch);
            
            // Process each user
            for (const user of users) {
                if (user) {
                    await this.processUser(user);
                }
            }
            
            // Small delay between batches
            await this.delay(100);
        }
        
        this.logger.info('Batch processing completed');
    }
    
    // Private helper methods
    private async makeApiCall<T>(
        endpoint: string, 
        method: 'GET' | 'POST' | 'PUT' | 'DELETE' = 'GET',
        data?: any
    ): Promise<T | null> {
        // Simulate API call
        await this.delay(Math.random() * 100 + 50);
        
        // Simulate occasional failures
        if (Math.random() < 0.1) {
            throw new Error(`API call failed: ${method} ${endpoint}`);
        }
        
        // Mock response based on endpoint
        if (endpoint.startsWith('/users/')) {
            const id = endpoint.split('/').pop()!;
            return {
                id,
                name: `User ${id}`,
                email: `user${id}@example.com`,
                createdAt: new Date(),
                updatedAt: new Date()
            } as T;
        }
        
        if (endpoint.startsWith('/users') && method === 'POST') {
            return {
                id: Math.random().toString(36).substring(2),
                ...data,
                createdAt: new Date(),
                updatedAt: new Date()
            } as T;
        }
        
        return null;
    }
    
    private async validateUserData(userData: CreateUserData): Promise<void> {
        await this.delay(10);
        
        if (!userData.name || userData.name.trim().length === 0) {
            throw new Error('Name is required');
        }
        
        if (!userData.email || !userData.email.includes('@')) {
            throw new Error('Valid email is required');
        }
    }
    
    private async processUser(user: User): Promise<void> {
        await this.delay(20);
        this.logger.debug(`Processed user: ${user.id}`);
    }
    
    private delay(ms: number): Promise<void> {
        return new Promise(resolve => setTimeout(resolve, ms));
    }
}

// Types for the async service
export interface User {
    id: string;
    name: string;
    email: string;
    createdAt: Date;
    updatedAt: Date;
}

export interface CreateUserData {
    name: string;
    email: string;
    metadata?: Record<string, any>;
}

export interface UserFetchResult {
    id: string;
    success: boolean;
    user?: User;
    error?: Error;
}

// Observable pattern with async
export class UserEventStream {
    private listeners: Set<(user: User) => void> = new Set();
    private isRunning = false;
    private userService: AsyncUserService;
    
    constructor(userService: AsyncUserService) {
        this.userService = userService;
    }
    
    subscribe(listener: (user: User) => void): () => void {
        this.listeners.add(listener);
        
        // Return unsubscribe function
        return () => {
            this.listeners.delete(listener);
        };
    }
    
    async start(): Promise<void> {
        if (this.isRunning) {
            return;
        }
        
        this.isRunning = true;
        
        try {
            for await (const user of this.userService.getUsersStream()) {
                if (!this.isRunning) {
                    break;
                }
                
                this.listeners.forEach(listener => {
                    try {
                        listener(user);
                    } catch (error) {
                        console.error('Error in user stream listener:', error);
                    }
                });
            }
        } finally {
            this.isRunning = false;
        }
    }
    
    stop(): void {
        this.isRunning = false;
    }
}

// Default export
export default AsyncUserService;