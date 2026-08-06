import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react";
import { ApiError, api } from "../lib/api";
import type { SessionUser, StaffRole } from "../types";

type SessionStatus =
  | "loading"
  | "bootstrapRequired"
  | "authenticated"
  | "unauthenticated"
  | "error";

export interface BootstrapValues {
  token: string;
  schoolName: string;
  username: string;
  displayName: string;
  password: string;
}

interface SessionContextValue {
  status: SessionStatus;
  user: SessionUser | null;
  error: Error | null;
  reload: () => Promise<void>;
  completeBootstrap: (values: BootstrapValues) => Promise<void>;
  login: (username: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  hasAnyRole: (...roles: StaffRole[]) => boolean;
}

const SessionContext = createContext<SessionContextValue | undefined>(undefined);

function normalizeSessionUser(value: SessionUser & { role?: StaffRole }) {
  return {
    ...value,
    roles: value.roles?.length ? value.roles : value.role ? [value.role] : [],
  };
}

export function SessionProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<SessionStatus>("loading");
  const [user, setUser] = useState<SessionUser | null>(null);
  const [error, setError] = useState<Error | null>(null);

  const reload = useCallback(async () => {
    setStatus("loading");
    setError(null);
    try {
      try {
        const bootstrap = await api.get<{
          state?: "pending" | "completed";
          bootstrapRequired?: boolean;
          completed?: boolean;
        }>("/bootstrap/status");
        const required =
          bootstrap.bootstrapRequired === true ||
          bootstrap.completed === false ||
          bootstrap.state === "pending";
        if (required) {
          setUser(null);
          setStatus("bootstrapRequired");
          return;
        }
      } catch (bootstrapError) {
        // The bootstrap route is deliberately host-local. A peer may receive a
        // concealed 404/403 and should continue to the normal sign-in check.
        if (
          !(
            bootstrapError instanceof ApiError &&
            [403, 404].includes(bootstrapError.status)
          )
        ) {
          throw bootstrapError;
        }
      }
      const current = await api.get<SessionUser>("/auth/me");
      setUser(normalizeSessionUser(current));
      setStatus("authenticated");
    } catch (reason) {
      setUser(null);
      if (reason instanceof ApiError && reason.status === 401) {
        setStatus("unauthenticated");
      } else {
        setError(
          reason instanceof Error
            ? reason
            : new Error("サーバーに接続できませんでした。"),
        );
        setStatus("error");
      }
    }
  }, []);

  useEffect(() => {
    void reload();
  }, [reload]);

  useEffect(() => {
    const updateExpiry = (event: Event) => {
      const expiresAt = (event as CustomEvent<{
        sessionExpiresAt?: string;
      }>).detail?.sessionExpiresAt;
      if (!expiresAt) return;
      setUser((current) =>
        current ? { ...current, sessionExpiresAt: expiresAt } : current,
      );
    };
    window.addEventListener("ooki:session-expiry", updateExpiry);
    return () =>
      window.removeEventListener("ooki:session-expiry", updateExpiry);
  }, []);

  const login = useCallback(
    async (username: string, password: string) => {
      await api.post<void>(
        "/auth/login",
        { username, password },
        { csrf: false },
      );
      await reload();
    },
    [reload],
  );

  const completeBootstrap = useCallback(
    async (values: BootstrapValues) => {
      await api.post<void>("/bootstrap/complete", values, { csrf: false });
      await reload();
    },
    [reload],
  );

  const logout = useCallback(async () => {
    await api.post<void>("/auth/logout");
    setUser(null);
    setStatus("unauthenticated");
  }, []);

  const hasAnyRole = useCallback(
    (...roles: StaffRole[]) =>
      Boolean(user?.roles.some((role) => roles.includes(role))),
    [user],
  );

  const value = useMemo(
    () => ({
      status,
      user,
      error,
      reload,
      completeBootstrap,
      login,
      logout,
      hasAnyRole,
    }),
    [
      status,
      user,
      error,
      reload,
      completeBootstrap,
      login,
      logout,
      hasAnyRole,
    ],
  );

  return (
    <SessionContext.Provider value={value}>{children}</SessionContext.Provider>
  );
}

export function useSession() {
  const context = useContext(SessionContext);
  if (!context) {
    throw new Error("useSession must be used inside SessionProvider.");
  }
  return context;
}
