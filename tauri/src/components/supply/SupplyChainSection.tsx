import { useTranslation } from "react-i18next";
import { SupplyChainGraph } from "./SupplyChainGraph";
import type { SupplyResponse } from "./supplyGraph";
import "./SupplyChain.css";

type Props = {
  data: SupplyResponse | null;
  loading: boolean;
  error: boolean;
};

/** Display the fetched supply graph without refetching on tab changes. */
export function SupplyChainSection({ data, loading, error }: Props) {
  const { t } = useTranslation();
  return <div className="supply-section" role="group" aria-label={t("supplyGraph.title")} aria-busy={loading}>
    {data?.pending ? <p className="supply-note">{t("supplyGraph.executing")}</p>
      : error ? <p className="supply-note" role="status">{t("supplyGraph.fetchError")}</p>
      : loading ? <p className="supply-note">{t("common.loading")}</p>
      : data?.graph && <SupplyChainGraph graph={data.graph} />}
  </div>;
}
