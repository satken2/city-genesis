import assert from "node:assert/strict";
import { PRODUCT_ELECTRICITY, PRODUCT_HIGH_ELECTRICITY, PRODUCT_HIGH_WATER, PRODUCT_WATER_SUPPLY, PRODUCT_FIRE_SERVICE } from "../src/constants";
import { getPublicServiceCapacity, getPublicServiceFacilities, getPublicServiceInfrastructure } from "../src/components/budget/publicServiceInfrastructure";
import type { PublicFacilityAgent, PublicServiceSupply } from "../src/types";

const facility = (agent_id: number, production_capacity: number | null): PublicFacilityAgent => ({
  agent_id, production_capacity, name: String(agent_id), variant: 1, status: 1, reserves: 0, balance: 0, utilization_rate: 0.5,
});
const supply = (product_id: number, facilities: PublicFacilityAgent[]): PublicServiceSupply => ({
  product_id, facilities, supply_capacity: 10, production: 9, demand: 8,
});
const supplies = {
  [PRODUCT_ELECTRICITY]: supply(PRODUCT_ELECTRICITY, [facility(1, 3000)]),
  [PRODUCT_HIGH_ELECTRICITY]: supply(PRODUCT_HIGH_ELECTRICITY, [facility(2, 2.5), facility(3, null)]),
  [PRODUCT_HIGH_WATER]: supply(PRODUCT_HIGH_WATER, [facility(4, 8)]),
};
assert.equal(getPublicServiceInfrastructure(PRODUCT_FIRE_SERVICE), undefined);
assert.equal(getPublicServiceInfrastructure(PRODUCT_WATER_SUPPLY)?.productId, PRODUCT_HIGH_WATER);
assert.equal(getPublicServiceInfrastructure(PRODUCT_WATER_SUPPLY)?.quantityScale, 1000);
assert.deepEqual(getPublicServiceFacilities(PRODUCT_ELECTRICITY, supplies).map(row => [row.agent_id, row.production_capacity]),
  [[1, 3000], [2, 2500], [3, null]], "変電所と発電所を並べ、MWhからkWhに揃える");
assert.deepEqual(getPublicServiceFacilities(PRODUCT_WATER_SUPPLY, supplies).map(row => [row.agent_id, row.production_capacity]), [[4, 8000]]);
assert.equal(supplies[PRODUCT_HIGH_ELECTRICITY].facilities![0].production_capacity, 2.5, "APIデータ自体は換算で書き換えない");
assert.deepEqual(getPublicServiceFacilities(PRODUCT_FIRE_SERVICE, supplies), []);
assert.deepEqual(getPublicServiceFacilities(PRODUCT_ELECTRICITY, {}), []);
const electricCapacity = (substation: number, powerPlant: number) => getPublicServiceCapacity(PRODUCT_ELECTRICITY, {
  [PRODUCT_ELECTRICITY]: { ...supplies[PRODUCT_ELECTRICITY], supply_capacity: substation },
  [PRODUCT_HIGH_ELECTRICITY]: { ...supplies[PRODUCT_HIGH_ELECTRICITY], supply_capacity: powerPlant },
});
assert.equal(electricCapacity(3000, 2), 2000, "発電容量が小さければ、MWhからkWhに換算した発電容量が上限になる");
assert.equal(electricCapacity(1000, 2), 1000, "変電容量が小さければ変電容量が上限になる");
assert.equal(getPublicServiceCapacity(PRODUCT_WATER_SUPPLY, {
  [PRODUCT_WATER_SUPPLY]: supply(PRODUCT_WATER_SUPPLY, []),
  [PRODUCT_HIGH_WATER]: { ...supplies[PRODUCT_HIGH_WATER], supply_capacity: 0.005 },
}), 5, "水道もKLからLに換算して小さい方を比較する");
assert.equal(getPublicServiceCapacity(PRODUCT_ELECTRICITY, {}), 0);
assert.equal(getPublicServiceCapacity(PRODUCT_ELECTRICITY, { [PRODUCT_ELECTRICITY]: supplies[PRODUCT_ELECTRICITY] }), 0,
  "高圧側の容量が無い場合は供給上限も0になる");
console.log("PASS: infrastructure facility selection and capacity units");
