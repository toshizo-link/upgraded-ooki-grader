import { useApiQuery } from "./useApiQuery";
import { api } from "../lib/api";
import type { RuntimeCapabilities } from "../types";

export function useRuntimeCapabilities() {
  return useApiQuery<RuntimeCapabilities>(
    "runtime-capabilities",
    (signal) => api.get("/capabilities", undefined, signal),
  );
}
