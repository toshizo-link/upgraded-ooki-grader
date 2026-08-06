import { useEffect, useState } from "react";

type StreamState = "connecting" | "connected" | "disconnected";

const eventNames = [
  "submission.status",
  "upload.status",
  "job.status",
  "review.counts",
  "export.status",
  "system.storageWarning",
  "system.providerWarning",
  "session.summaryChanged",
];

export function useStatusStream(enabled: boolean) {
  const [state, setState] = useState<StreamState>("connecting");
  const [storageWarning, setStorageWarning] = useState(false);

  useEffect(() => {
    if (!enabled) return;
    const source = new EventSource("/api/v1/events", {
      withCredentials: true,
    });

    source.onopen = () => setState("connected");
    source.onerror = () => setState("disconnected");

    const handleEvent = (event: Event) => {
      const message = event as MessageEvent<string>;
      let detail: unknown;
      try {
        detail = JSON.parse(message.data);
      } catch {
        detail = {};
      }
      if (message.type === "system.storageWarning") {
        setStorageWarning(true);
      }
      window.dispatchEvent(
        new CustomEvent("ooki:status", {
          detail: { type: message.type, payload: detail },
        }),
      );
    };

    eventNames.forEach((name) => source.addEventListener(name, handleEvent));
    return () => {
      eventNames.forEach((name) =>
        source.removeEventListener(name, handleEvent),
      );
      source.close();
    };
  }, [enabled]);

  return { state, storageWarning };
}
