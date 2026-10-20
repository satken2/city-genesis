export type ClimateMonth = {
  temperature: number;
  precipitation: number;
  sunlight: number;
  weather: Record<string, number>;
};

/** Actual data stored in cities/scenarios, without a preset ID. */
export type ClimateSettings = { name: string; months: ClimateMonth[] };
export type ClimatePreset = { display_name: string; months: ClimateMonth[] };
export type WeatherMasterEntry = { display_name: string };

export function copyClimatePreset(preset: ClimatePreset): ClimateSettings {
  return { name: preset.display_name,
    months: preset.months.map(month => ({ ...month, weather: { ...month.weather } })) };
}

/** Moving a boundary exchanges proportions only between its two neighbors, preserving other weather shares and the 100% total. */
export function moveWeatherBoundary(weather: Record<string, number>, left: string, right: string, delta: number) {
  const movement = Math.max(-weather[left], Math.min(weather[right], Math.round(delta)));
  return { ...weather, [left]: weather[left] + movement, [right]: weather[right] - movement };
}

/** Numeric input redistributes proportionally among other weather types, allocating integer remainders from largest to smallest. Zero-percent entries are editable too. */
export function setWeatherPercentage(weather: Record<string, number>, id: string, requested: number) {
  const value = Math.max(0, Math.min(100, Math.round(requested)));
  const other = Object.keys(weather).filter(key => key !== id);
  const remaining = 100 - value;
  const total = other.reduce((sum, key) => sum + weather[key], 0);
  const shares = other.map(key => ({ key, exact: remaining * (total > 0 ? weather[key] / total : 1 / other.length) }));
  const result: Record<string, number> = { [id]: value };
  for (const share of shares) result[share.key] = Math.floor(share.exact);
  const difference = remaining - other.reduce((sum, key) => sum + result[key], 0);
  shares.sort((a, b) => (b.exact - Math.floor(b.exact)) - (a.exact - Math.floor(a.exact)));
  for (let i = 0; i < difference; i++) result[shares[i].key]++;
  return Object.fromEntries(Object.keys(weather).map(key => [key, result[key]]));
}
