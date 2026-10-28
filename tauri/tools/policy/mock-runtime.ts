export const opened: string[] = [];
export const invocations: { windowType: string; params?: Record<string, unknown> }[] = [];
const handlers = new Map<string, Set<(event: { payload: unknown }) => void>>();
export async function invoke(_command: string, args: { windowType: string; params?: Record<string, unknown> }) {
  opened.push(args.windowType);
  invocations.push(args);
}
export async function listen(name: string, fn: (event: { payload: unknown }) => void) {
  if (!handlers.has(name)) handlers.set(name, new Set());
  handlers.get(name)!.add(fn);
  return () => { handlers.get(name)!.delete(fn); };
}
export function getCurrentWindow() { return { isVisible: async () => true }; }
export function dispatch(name: string, payload: unknown) {
  for (const fn of handlers.get(name) ?? []) fn({ payload });
}
