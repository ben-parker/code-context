export class UserService {
    private users: User[] = [];
    
    public getUser(id: string): User | null {
        return this.users.find(u => u.id === id) || null;
    }
    
    public addUser(user: User): void {
        this.users.push(user);
    }
}

export interface User {
    id: string;
    name: string;
    email: string;
}