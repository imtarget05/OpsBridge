import React, { createContext, useContext, useEffect, useState } from 'react';
import { api } from '../../lib/api-client';
import type { User, UserRole } from '../../types';

interface AuthContextType {
  user: User | null;
  role: UserRole | null;
  isLoading: boolean;
  login: (username: string, role?: UserRole) => Promise<void>;
  loginWithGoogle: (credential: string) => Promise<void>;
  logout: () => void;
  switchPersona: (role: UserRole) => Promise<void>;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [isLoading, setIsLoading] = useState<boolean>(true);

  useEffect(() => {
    const initAuth = async () => {
      const token = api.getToken();
      if (!token) {
        setIsLoading(false);
        return;
      }

      try {
        const currentUser = await api.getMe();
        setUser(currentUser);
      } catch {
        api.logout();
        setUser(null);
      } finally {
        setIsLoading(false);
      }
    };

    initAuth();
  }, []);

  const login = async (username: string, role?: UserRole) => {
    setIsLoading(true);
    try {
      const res = await api.login(username, role);
      setUser(res.user);
    } finally {
      setIsLoading(false);
    }
  };

  const loginWithGoogle = async (credential: string) => {
    setIsLoading(true);
    try {
      const res = await api.loginWithGoogle(credential);
      setUser(res.user);
    } finally {
      setIsLoading(false);
    }
  };

  const logout = () => {
    api.logout();
    setUser(null);
  };

  const switchPersona = async (newRole: UserRole) => {
    const username = `${newRole.toLowerCase()}_user`;
    await login(username, newRole);
  };

  return (
    <AuthContext.Provider
      value={{
        user,
        role: user?.role || null,
        isLoading,
        login,
        loginWithGoogle,
        logout,
        switchPersona,
      }}
    >
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error('useAuth must be used within an AuthProvider');
  }
  return context;
}
