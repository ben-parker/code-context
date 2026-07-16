import { EventEmitter } from 'events';
import { Logger } from './Logger';

// Base interface
export interface Entity {
    id: string;
    createdAt: Date;
    updatedAt: Date;
}

// Extended interface
export interface UserEntity extends Entity {
    name: string;
    email: string;
    role: UserRole;
}

// Enum
export enum UserRole {
    Admin = 'admin',
    Moderator = 'moderator',
    User = 'user',
    Guest = 'guest'
}

// Abstract base class
export abstract class BaseRepository<T extends Entity> {
    protected abstract tableName: string;
    protected logger: Logger;
    protected eventEmitter: EventEmitter;
    
    constructor(logger: Logger, eventEmitter: EventEmitter) {
        this.logger = logger;
        this.eventEmitter = eventEmitter;
    }
    
    abstract async findById(id: string): Promise<T | null>;
    abstract async save(entity: T): Promise<T>;
    abstract async delete(id: string): Promise<boolean>;
    
    protected log(message: string, level: 'info' | 'error' | 'debug' = 'info'): void {
        this.logger.log(message, level);
    }
    
    protected emit(event: string, data: any): void {
        this.eventEmitter.emit(event, data);
    }
}

// Generic interface
export interface Repository<T> {
    findById(id: string): Promise<T | null>;
    save(entity: T): Promise<T>;
    delete(id: string): Promise<boolean>;
    findAll(): Promise<T[]>;
}

// Concrete implementation
export class UserRepository extends BaseRepository<UserEntity> implements Repository<UserEntity> {
    protected tableName = 'users';
    private users: Map<string, UserEntity> = new Map();
    
    constructor(logger: Logger, eventEmitter: EventEmitter) {
        super(logger, eventEmitter);
        this.log('UserRepository initialized');
    }
    
    async findById(id: string): Promise<UserEntity | null> {
        this.log(`Finding user by id: ${id}`);
        const user = this.users.get(id);
        if (user) {
            this.emit('userFound', user);
        }
        return user || null;
    }
    
    async save(entity: UserEntity): Promise<UserEntity> {
        this.log(`Saving user: ${entity.name}`);
        entity.updatedAt = new Date();
        this.users.set(entity.id, entity);
        this.emit('userSaved', entity);
        return entity;
    }
    
    async delete(id: string): Promise<boolean> {
        this.log(`Deleting user: ${id}`);
        const deleted = this.users.delete(id);
        if (deleted) {
            this.emit('userDeleted', { id });
        }
        return deleted;
    }
    
    async findAll(): Promise<UserEntity[]> {
        this.log('Finding all users');
        return Array.from(this.users.values());
    }
    
    async findByRole(role: UserRole): Promise<UserEntity[]> {
        this.log(`Finding users by role: ${role}`);
        return Array.from(this.users.values()).filter(user => user.role === role);
    }
    
    static validateEmail(email: string): boolean {
        const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
        return emailRegex.test(email);
    }
    
    static createDefaultUser(): UserEntity {
        return {
            id: crypto.randomUUID(),
            name: 'Default User',
            email: 'user@example.com',
            role: UserRole.User,
            createdAt: new Date(),
            updatedAt: new Date()
        };
    }
}

// Another concrete implementation
export class AdminRepository extends UserRepository {
    constructor(logger: Logger, eventEmitter: EventEmitter) {
        super(logger, eventEmitter);
        this.log('AdminRepository initialized');
    }
    
    async findAll(): Promise<UserEntity[]> {
        this.log('Finding all users (admin access)');
        const users = await super.findAll();
        this.emit('adminAccess', { action: 'findAll', count: users.length });
        return users;
    }
    
    async findAdmins(): Promise<UserEntity[]> {
        return await this.findByRole(UserRole.Admin);
    }
    
    async promoteUser(id: string): Promise<UserEntity | null> {
        const user = await this.findById(id);
        if (user && user.role !== UserRole.Admin) {
            user.role = UserRole.Admin;
            await this.save(user);
            this.emit('userPromoted', user);
            return user;
        }
        return null;
    }
}

// Utility type aliases
export type UserCreateData = Omit<UserEntity, 'id' | 'createdAt' | 'updatedAt'>;
export type UserUpdateData = Partial<Pick<UserEntity, 'name' | 'email' | 'role'>>;
export type UserSearchCriteria = {
    name?: string;
    email?: string;
    role?: UserRole;
    createdAfter?: Date;
    createdBefore?: Date;
};

// Factory pattern
export class RepositoryFactory {
    private logger: Logger;
    private eventEmitter: EventEmitter;
    
    constructor(logger: Logger, eventEmitter: EventEmitter) {
        this.logger = logger;
        this.eventEmitter = eventEmitter;
    }
    
    createUserRepository(): UserRepository {
        return new UserRepository(this.logger, this.eventEmitter);
    }
    
    createAdminRepository(): AdminRepository {
        return new AdminRepository(this.logger, this.eventEmitter);
    }
}