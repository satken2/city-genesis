export const dialog = { selected: null as string | null, calls: [] as string[] };
export async function open() { dialog.calls.push("open"); return dialog.selected; }
export async function save() { dialog.calls.push("save"); return dialog.selected; }
