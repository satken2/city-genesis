import { PRODUCT_ELECTRICITY, PRODUCT_HIGH_ELECTRICITY, PRODUCT_HIGH_WATER, PRODUCT_WATER_SUPPLY } from "../../constants";
import type { PublicFacilityAgent, PublicServiceSupply } from "../../types";

type ServiceInfrastructure = {
  productId: number;
  quantityScale: number;
  capacityLabel: string;
  infrastructureCapacityLabel: string;
};

/** 高圧財のMWh・KLを、公共サービスのkWh・Lと比較するための対応。 */
const INFRASTRUCTURE_BY_SERVICE: Partial<Record<number, ServiceInfrastructure>> = {
  [PRODUCT_ELECTRICITY]: { productId: PRODUCT_HIGH_ELECTRICITY, quantityScale: 1000, capacityLabel: "substationCapacity", infrastructureCapacityLabel: "powerPlantCapacity" },
  [PRODUCT_WATER_SUPPLY]: { productId: PRODUCT_HIGH_WATER, quantityScale: 1000, capacityLabel: "waterDistributionCapacity", infrastructureCapacityLabel: "waterIntakeCapacity" },
};

export function getPublicServiceInfrastructure(productId: number) {
  return INFRASTRUCTURE_BY_SERVICE[productId];
}

/** サービス側の単位に揃えた容量のうち、小さい方を供給上限として比較する。 */
export function getPublicServiceCapacity(productId: number, supplies: Record<number, PublicServiceSupply>): number {
  const capacity = supplies[productId]?.supply_capacity ?? 0;
  const infrastructure = getPublicServiceInfrastructure(productId);
  if (!infrastructure) return capacity;
  return Math.min(capacity, (supplies[infrastructure.productId]?.supply_capacity ?? 0) * infrastructure.quantityScale);
}

/** 一覧の容量もグラフと同じサービス側の単位に揃え、取得データ自体は変更しない。 */
export function getPublicServiceFacilities(productId: number, supplies: Record<number, PublicServiceSupply>): PublicFacilityAgent[] {
  const facilities = supplies[productId]?.facilities ?? [];
  const infrastructure = getPublicServiceInfrastructure(productId);
  if (!infrastructure) return facilities;
  return [...facilities, ...(supplies[infrastructure.productId]?.facilities ?? []).map((facility) => ({
    ...facility,
    production_capacity: facility.production_capacity === null ? null : facility.production_capacity * infrastructure.quantityScale,
  }))];
}
