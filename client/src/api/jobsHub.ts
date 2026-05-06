import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";

export interface JobLogEvent { jobId: string; timestamp: string; level: string; message: string }
export interface JobStatusEvent {
  jobId: string;
  status: string;
  progress: number | null;
  done: number | null;
  total: number | null;
}

export interface JobHubHandle {
  conn: HubConnection;
  start(): Promise<void>;
  subscribe(jobId: string): Promise<void>;
  unsubscribe(jobId: string): Promise<void>;
  stop(): Promise<void>;
  onLog(cb: (e: JobLogEvent) => void): () => void;
  onStatus(cb: (e: JobStatusEvent) => void): () => void;
}

export function createJobHub(): JobHubHandle {
  const conn = new HubConnectionBuilder()
    .withUrl("/hubs/jobs")
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();

  return {
    conn,
    async start() {
      if (conn.state === HubConnectionState.Disconnected) await conn.start();
    },
    subscribe(jobId)   { return conn.invoke("SubscribeJob", jobId); },
    unsubscribe(jobId) { return conn.invoke("UnsubscribeJob", jobId); },
    stop()             { return conn.stop(); },
    onLog(cb)          { conn.on("log", cb); return () => conn.off("log", cb); },
    onStatus(cb)       { conn.on("status", cb); return () => conn.off("status", cb); },
  };
}
