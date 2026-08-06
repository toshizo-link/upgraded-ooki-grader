import {
  Children,
  createContext,
  isValidElement,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type AnchorHTMLAttributes,
  type MouseEvent,
  type ReactElement,
  type ReactNode,
} from "react";

interface LocationSnapshot {
  pathname: string;
  search: string;
  hash: string;
  state: unknown;
}

interface NavigateOptions {
  replace?: boolean;
  state?: unknown;
}

type Navigate = (to: string, options?: NavigateOptions) => void;

interface RouterValue {
  location: LocationSnapshot;
  navigate: Navigate;
}

const RouterContext = createContext<RouterValue | null>(null);
const ParamsContext = createContext<Record<string, string>>({});

function currentLocation(): LocationSnapshot {
  return {
    pathname: window.location.pathname,
    search: window.location.search,
    hash: window.location.hash,
    state: window.history.state,
  };
}

export function BrowserRouter({ children }: { children: ReactNode }) {
  const [location, setLocation] = useState(currentLocation);

  useEffect(() => {
    const handlePopState = () => setLocation(currentLocation());
    window.addEventListener("popstate", handlePopState);
    return () => window.removeEventListener("popstate", handlePopState);
  }, []);

  const navigate = useCallback<Navigate>((to, options = {}) => {
    const target = new URL(to, window.location.href);
    if (target.origin !== window.location.origin) {
      window.location.assign(target.href);
      return;
    }

    const href = `${target.pathname}${target.search}${target.hash}`;
    if (options.replace) {
      window.history.replaceState(options.state ?? null, "", href);
    } else {
      window.history.pushState(options.state ?? null, "", href);
    }
    setLocation(currentLocation());
  }, []);

  const value = useMemo(
    () => ({ location, navigate }),
    [location, navigate],
  );
  return (
    <RouterContext.Provider value={value}>
      {children}
    </RouterContext.Provider>
  );
}

function useRouter() {
  const router = useContext(RouterContext);
  if (!router) {
    throw new Error("Router hooks must be used inside BrowserRouter.");
  }
  return router;
}

export interface RouteProps {
  path: string;
  element: ReactNode;
}

export function Route(_: RouteProps) {
  return null;
}

export function Routes({ children }: { children: ReactNode }) {
  const { location } = useRouter();
  for (const child of Children.toArray(children)) {
    if (!isValidElement<RouteProps>(child) || child.type !== Route) {
      continue;
    }
    const params = matchPath(child.props.path, location.pathname);
    if (params) {
      return (
        <ParamsContext.Provider value={params}>
          {child.props.element}
        </ParamsContext.Provider>
      );
    }
  }
  return null;
}

function matchPath(
  pattern: string,
  pathname: string,
): Record<string, string> | null {
  if (pattern === "*") return {};
  const patternParts = segments(pattern);
  const pathParts = segments(pathname);
  if (patternParts.length !== pathParts.length) return null;

  const params: Record<string, string> = {};
  for (let index = 0; index < patternParts.length; index++) {
    const expected = patternParts[index];
    const actual = pathParts[index];
    if (expected?.startsWith(":")) {
      if (actual === undefined) return null;
      try {
        params[expected.slice(1)] = decodeURIComponent(actual);
      } catch {
        return null;
      }
    } else if (expected !== actual) {
      return null;
    }
  }
  return params;
}

function segments(value: string) {
  return value.split("/").filter(Boolean);
}

interface LinkProps
  extends Omit<AnchorHTMLAttributes<HTMLAnchorElement>, "href"> {
  to: string;
}

export function Link({
  to,
  onClick,
  target,
  children,
  ...props
}: LinkProps) {
  const { navigate } = useRouter();
  const handleClick = (event: MouseEvent<HTMLAnchorElement>) => {
    onClick?.(event);
    if (
      event.defaultPrevented ||
      event.button !== 0 ||
      event.metaKey ||
      event.ctrlKey ||
      event.shiftKey ||
      event.altKey ||
      (target && target !== "_self")
    ) {
      return;
    }

    const targetUrl = new URL(to, window.location.href);
    if (targetUrl.origin !== window.location.origin) return;
    event.preventDefault();
    navigate(to);
  };

  return (
    <a href={to} target={target} onClick={handleClick} {...props}>
      {children}
    </a>
  );
}

interface NavLinkState {
  isActive: boolean;
}

interface NavLinkProps extends Omit<LinkProps, "className"> {
  end?: boolean;
  className?: string | ((state: NavLinkState) => string | undefined);
}

export function NavLink({
  to,
  end = false,
  className,
  ...props
}: NavLinkProps) {
  const { location } = useRouter();
  const targetPath = new URL(to, window.location.href).pathname;
  const isActive = end
    ? location.pathname === targetPath
    : location.pathname === targetPath ||
      (targetPath !== "/" && location.pathname.startsWith(`${targetPath}/`));
  const resolvedClassName =
    typeof className === "function" ? className({ isActive }) : className;
  return (
    <Link
      to={to}
      className={resolvedClassName}
      aria-current={isActive ? "page" : undefined}
      {...props}
    />
  );
}

export function useLocation() {
  return useRouter().location;
}

export function useNavigate() {
  return useRouter().navigate;
}

export function useParams<
  T extends Record<string, string | undefined> = Record<
    string,
    string | undefined
  >,
>() {
  return useContext(ParamsContext) as T;
}

type SearchParamsInitializer =
  | URLSearchParams
  | string
  | Record<string, string>;
type SearchParamsSetter = (
  next:
    | SearchParamsInitializer
    | ((current: URLSearchParams) => SearchParamsInitializer),
  options?: NavigateOptions,
) => void;

export function useSearchParams(): [URLSearchParams, SearchParamsSetter] {
  const { location, navigate } = useRouter();
  const params = useMemo(
    () => new URLSearchParams(location.search),
    [location.search],
  );
  const setParams = useCallback<SearchParamsSetter>(
    (next, options) => {
      const resolved =
        typeof next === "function"
          ? next(new URLSearchParams(location.search))
          : next;
      const search = new URLSearchParams(resolved).toString();
      navigate(
        `${location.pathname}${search ? `?${search}` : ""}${location.hash}`,
        options,
      );
    },
    [location.hash, location.pathname, location.search, navigate],
  );
  return [params, setParams];
}

export type RouteElement = ReactElement<RouteProps>;
