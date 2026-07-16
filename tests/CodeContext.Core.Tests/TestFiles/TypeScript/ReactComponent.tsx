import React, { useState, useEffect, useCallback, useMemo, useRef, useContext } from 'react';
import { User } from './AsyncPatterns';
import { Logger } from './Logger';

// Context
interface UserContextType {
    currentUser: User | null;
    setCurrentUser: (user: User | null) => void;
    isAuthenticated: boolean;
}

const UserContext = React.createContext<UserContextType | undefined>(undefined);

// Custom hook
export function useUser(): UserContextType {
    const context = useContext(UserContext);
    if (!context) {
        throw new Error('useUser must be used within a UserProvider');
    }
    return context;
}

// Provider component
export function UserProvider({ children }: { children: React.ReactNode }) {
    const [currentUser, setCurrentUser] = useState<User | null>(null);
    
    const isAuthenticated = useMemo(() => currentUser !== null, [currentUser]);
    
    const contextValue = useMemo<UserContextType>(() => ({
        currentUser,
        setCurrentUser,
        isAuthenticated
    }), [currentUser, isAuthenticated]);
    
    return (
        <UserContext.Provider value={contextValue}>
            {children}
        </UserContext.Provider>
    );
}

// Props interfaces
interface UserListProps {
    users: User[];
    onUserSelect: (user: User) => void;
    onUserDelete: (userId: string) => void;
    loading?: boolean;
    error?: string;
}

interface UserFormProps {
    user?: User;
    onSubmit: (userData: Partial<User>) => void;
    onCancel: () => void;
    isLoading?: boolean;
}

// Component with hooks
export function UserList({ users, onUserSelect, onUserDelete, loading, error }: UserListProps) {
    const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
    const [searchTerm, setSearchTerm] = useState('');
    const logger = useRef(new Logger('UserList'));
    
    // Filtered users
    const filteredUsers = useMemo(() => {
        if (!searchTerm) return users;
        
        return users.filter(user =>
            user.name.toLowerCase().includes(searchTerm.toLowerCase()) ||
            user.email.toLowerCase().includes(searchTerm.toLowerCase())
        );
    }, [users, searchTerm]);
    
    // Handle selection
    const handleToggleSelection = useCallback((userId: string) => {
        setSelectedIds(prev => {
            const newSet = new Set(prev);
            if (newSet.has(userId)) {
                newSet.delete(userId);
            } else {
                newSet.add(userId);
            }
            return newSet;
        });
    }, []);
    
    // Handle bulk delete
    const handleBulkDelete = useCallback(() => {
        if (selectedIds.size === 0) return;
        
        const confirmed = window.confirm(`Delete ${selectedIds.size} users?`);
        if (confirmed) {
            selectedIds.forEach(id => onUserDelete(id));
            setSelectedIds(new Set());
        }
    }, [selectedIds, onUserDelete]);
    
    // Log when users change
    useEffect(() => {
        logger.current.info(`User list updated: ${users.length} users`);
    }, [users]);
    
    // Cleanup on unmount
    useEffect(() => {
        return () => {
            logger.current.info('UserList component unmounted');
        };
    }, []);
    
    if (loading) {
        return (
            <div className="user-list loading">
                <div className="spinner">Loading users...</div>
            </div>
        );
    }
    
    if (error) {
        return (
            <div className="user-list error">
                <div className="error-message">Error: {error}</div>
                <button onClick={() => window.location.reload()}>
                    Retry
                </button>
            </div>
        );
    }
    
    return (
        <div className="user-list">
            <div className="user-list__header">
                <input
                    type="text"
                    placeholder="Search users..."
                    value={searchTerm}
                    onChange={(e) => setSearchTerm(e.target.value)}
                    className="user-list__search"
                />
                
                {selectedIds.size > 0 && (
                    <button
                        onClick={handleBulkDelete}
                        className="user-list__bulk-delete"
                    >
                        Delete Selected ({selectedIds.size})
                    </button>
                )}
            </div>
            
            <div className="user-list__content">
                {filteredUsers.length === 0 ? (
                    <div className="user-list__empty">
                        {searchTerm ? 'No users found matching your search.' : 'No users available.'}
                    </div>
                ) : (
                    filteredUsers.map(user => (
                        <UserCard
                            key={user.id}
                            user={user}
                            selected={selectedIds.has(user.id)}
                            onSelect={() => onUserSelect(user)}
                            onToggleSelection={() => handleToggleSelection(user.id)}
                            onDelete={() => onUserDelete(user.id)}
                        />
                    ))
                )}
            </div>
        </div>
    );
}

// Individual user card component
interface UserCardProps {
    user: User;
    selected: boolean;
    onSelect: () => void;
    onToggleSelection: () => void;
    onDelete: () => void;
}

function UserCard({ user, selected, onSelect, onToggleSelection, onDelete }: UserCardProps) {
    const handleDeleteClick = useCallback((e: React.MouseEvent) => {
        e.stopPropagation();
        const confirmed = window.confirm(`Delete user ${user.name}?`);
        if (confirmed) {
            onDelete();
        }
    }, [user.name, onDelete]);
    
    return (
        <div
            className={`user-card ${selected ? 'selected' : ''}`}
            onClick={onSelect}
        >
            <div className="user-card__checkbox">
                <input
                    type="checkbox"
                    checked={selected}
                    onChange={onToggleSelection}
                    onClick={(e) => e.stopPropagation()}
                />
            </div>
            
            <div className="user-card__info">
                <h3 className="user-card__name">{user.name}</h3>
                <p className="user-card__email">{user.email}</p>
                <p className="user-card__date">
                    Created: {user.createdAt.toLocaleDateString()}
                </p>
            </div>
            
            <div className="user-card__actions">
                <button
                    onClick={handleDeleteClick}
                    className="user-card__delete"
                    aria-label={`Delete ${user.name}`}
                >
                    ×
                </button>
            </div>
        </div>
    );
}

// Form component
export function UserForm({ user, onSubmit, onCancel, isLoading }: UserFormProps) {
    const [formData, setFormData] = useState({
        name: user?.name || '',
        email: user?.email || ''
    });
    
    const [errors, setErrors] = useState<Record<string, string>>({});
    const logger = useRef(new Logger('UserForm'));
    
    // Validate form
    const validateForm = useCallback(() => {
        const newErrors: Record<string, string> = {};
        
        if (!formData.name.trim()) {
            newErrors.name = 'Name is required';
        }
        
        if (!formData.email.trim()) {
            newErrors.email = 'Email is required';
        } else if (!formData.email.includes('@')) {
            newErrors.email = 'Please enter a valid email';
        }
        
        setErrors(newErrors);
        return Object.keys(newErrors).length === 0;
    }, [formData]);
    
    // Handle form submission
    const handleSubmit = useCallback((e: React.FormEvent) => {
        e.preventDefault();
        
        if (!validateForm()) {
            logger.current.warn('Form validation failed');
            return;
        }
        
        logger.current.info('Submitting user form');
        onSubmit(formData);
    }, [formData, validateForm, onSubmit]);
    
    // Handle input changes
    const handleInputChange = useCallback((field: string, value: string) => {
        setFormData(prev => ({ ...prev, [field]: value }));
        
        // Clear error when user starts typing
        if (errors[field]) {
            setErrors(prev => ({ ...prev, [field]: '' }));
        }
    }, [errors]);
    
    // Effect for initialization
    useEffect(() => {
        if (user) {
            setFormData({
                name: user.name,
                email: user.email
            });
            logger.current.info(`Form initialized for user: ${user.id}`);
        }
    }, [user]);
    
    return (
        <form onSubmit={handleSubmit} className="user-form">
            <div className="user-form__field">
                <label htmlFor="name">Name:</label>
                <input
                    id="name"
                    type="text"
                    value={formData.name}
                    onChange={(e) => handleInputChange('name', e.target.value)}
                    className={errors.name ? 'error' : ''}
                    disabled={isLoading}
                />
                {errors.name && <span className="error-text">{errors.name}</span>}
            </div>
            
            <div className="user-form__field">
                <label htmlFor="email">Email:</label>
                <input
                    id="email"
                    type="email"
                    value={formData.email}
                    onChange={(e) => handleInputChange('email', e.target.value)}
                    className={errors.email ? 'error' : ''}
                    disabled={isLoading}
                />
                {errors.email && <span className="error-text">{errors.email}</span>}
            </div>
            
            <div className="user-form__actions">
                <button
                    type="submit"
                    disabled={isLoading}
                    className="user-form__submit"
                >
                    {isLoading ? 'Saving...' : (user ? 'Update' : 'Create')} User
                </button>
                
                <button
                    type="button"
                    onClick={onCancel}
                    disabled={isLoading}
                    className="user-form__cancel"
                >
                    Cancel
                </button>
            </div>
        </form>
    );
}

// Main application component
export function UserApp() {
    const [users, setUsers] = useState<User[]>([]);
    const [selectedUser, setSelectedUser] = useState<User | null>(null);
    const [isLoading, setIsLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [showForm, setShowForm] = useState(false);
    
    const logger = useRef(new Logger('UserApp'));
    
    // Load users
    const loadUsers = useCallback(async () => {
        setIsLoading(true);
        setError(null);
        
        try {
            // Simulate API call
            await new Promise(resolve => setTimeout(resolve, 1000));
            
            const mockUsers: User[] = [
                {
                    id: '1',
                    name: 'John Doe',
                    email: 'john@example.com',
                    createdAt: new Date(),
                    updatedAt: new Date()
                },
                {
                    id: '2',
                    name: 'Jane Smith',
                    email: 'jane@example.com',
                    createdAt: new Date(),
                    updatedAt: new Date()
                }
            ];
            
            setUsers(mockUsers);
            logger.current.info(`Loaded ${mockUsers.length} users`);
        } catch (err) {
            const errorMessage = err instanceof Error ? err.message : 'Unknown error';
            setError(errorMessage);
            logger.current.error('Failed to load users', err as Error);
        } finally {
            setIsLoading(false);
        }
    }, []);
    
    // Handle user operations
    const handleUserSelect = useCallback((user: User) => {
        setSelectedUser(user);
        setShowForm(true);
    }, []);
    
    const handleUserDelete = useCallback((userId: string) => {
        setUsers(prev => prev.filter(u => u.id !== userId));
        if (selectedUser?.id === userId) {
            setSelectedUser(null);
            setShowForm(false);
        }
        logger.current.info(`Deleted user: ${userId}`);
    }, [selectedUser]);
    
    const handleUserSubmit = useCallback((userData: Partial<User>) => {
        if (selectedUser) {
            // Update existing user
            setUsers(prev => prev.map(u =>
                u.id === selectedUser.id
                    ? { ...u, ...userData, updatedAt: new Date() }
                    : u
            ));
            logger.current.info(`Updated user: ${selectedUser.id}`);
        } else {
            // Create new user
            const newUser: User = {
                id: Math.random().toString(36).substring(2),
                ...userData as User,
                createdAt: new Date(),
                updatedAt: new Date()
            };
            setUsers(prev => [...prev, newUser]);
            logger.current.info(`Created user: ${newUser.id}`);
        }
        
        setShowForm(false);
        setSelectedUser(null);
    }, [selectedUser]);
    
    // Load users on mount
    useEffect(() => {
        loadUsers();
    }, [loadUsers]);
    
    return (
        <UserProvider>
            <div className="user-app">
                <header className="user-app__header">
                    <h1>User Management</h1>
                    <button
                        onClick={() => {
                            setSelectedUser(null);
                            setShowForm(true);
                        }}
                        className="user-app__add-user"
                    >
                        Add User
                    </button>
                </header>
                
                <main className="user-app__main">
                    {showForm ? (
                        <UserForm
                            user={selectedUser || undefined}
                            onSubmit={handleUserSubmit}
                            onCancel={() => {
                                setShowForm(false);
                                setSelectedUser(null);
                            }}
                            isLoading={isLoading}
                        />
                    ) : (
                        <UserList
                            users={users}
                            onUserSelect={handleUserSelect}
                            onUserDelete={handleUserDelete}
                            loading={isLoading}
                            error={error}
                        />
                    )}
                </main>
            </div>
        </UserProvider>
    );
}

export default UserApp;