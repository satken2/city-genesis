import { PRODUCT_ELECTRICITY, PRODUCT_HIGH_ELECTRICITY, PRODUCT_HIGH_WATER, PRODUCT_WATER_SUPPLY } from "../../constants";
import type { PublicFacilityAgent, PublicServiceSupply } from "../../types";

type ServiceInfrastructure = {
  productId: number;
  quantityScale: number;
  capacityLabel: string;
  infrastructureCapacityLabel: string;
};

/** Conversion between high-pressure goods in MWh/KL and public services in kWh/L for comparison. */
const INFRASTRUCTURE_BY_SERVICE: Partial<Record<number, ServiceInfrastructure>> = {
  [PRODUCT_ELECTRICITY]: { productId: PRODUCT_HIGH_ELECTRICITY, quantityScale: 1000, capacityLabel: "substationCapacity", infrastructureCapacityLabel: "powerPlantCapacity" },
  [PRODUCT_WATER_SUPPLY]: { productId: PRODUCT_HIGH_WATER, quantityScale: 1000, capacityLabel: "waterDistributionCapacity", infrastructureCapacityLabel: "waterIntakeCapacity" },
};

export function getPublicServiceInfrastructure(productId: number) {
  return INFRASTRUCTURE_BY_SERVICE[productId];
}

/** Compare capacities in service-side units and use the smaller one as the supply limit. */
export function getPublicServiceCapacity(productId: number, supplies: Record<number, PublicServiceSupply>): number {
  const capacity = supplies[productId]?.supply_capacity ?? 0;
  const infrastructure = getPublicServiceInfrastructure(productId);
  if (!infrastructure) return capacity;
  return Math.min(capacity, (supplies[infrastructure.productId]?.supply_capacity ?? 0) * infrastructure.quantityScale);
}

/** Convert list capacities to the same service-side units as the chart without modifying fetched data. */
export function getPublicServiceFacilities(productId: number, supplies: Record<number, PublicServiceSupply>): PublicFacilityAgent[] {
  const facilities = supplies[productId]?.facilities ?? [];
  const infrastructure = getPublicServiceInfrastructure(productId);
  if (!infrastructure) return facilities;
  return [...facilities, ...(supplies[infrastructure.productId]?.facilities ?? []).map((facility) => ({
    ...facility,
    production_capacity: facility.production_capacity === null ? null : facility.production_capacity * infrastructure.quantityScale,
  }))];
}
