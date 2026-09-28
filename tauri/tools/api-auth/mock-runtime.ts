export const runtime = { token: "new" as string | null, calls: [] as string[] };
export async function invoke(command: string) {
  runtime.calls.push(command);
  if (command === "get_connection_status") return { api_base_url: "http://127.0.0.1:5000", api_token: runtime.token, open_window_count: 0 };
  return { error: null };
}
