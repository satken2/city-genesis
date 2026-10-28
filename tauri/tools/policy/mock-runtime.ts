export const opened: string[] = [];
const handlers = new Map<string, Set<(event: { payload: unknown }) => void>>();
export async function invoke(_command: string, args: { windowType: string }) { opened.push(args.windowType); }
export async function listen(name: string, fn: (event: { payload: unknown }) => void) {
  if (!handlers.has(name)) handlers.set(name, new Set());
  handlers.get(name)!.add(fn);
  return () => { handlers.get(name)!.delete(fn); };
}
export function getCurrentWindow() { return { isVisible: async () => true }; }
export function dispatch(name: string, payload: unknown) {
  for (const fn of handlers.get(name) ?? []) fn({ payload });
}
