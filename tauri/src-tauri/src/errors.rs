use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

/// HTTP とコマンドの境界で診断文を表示用データへ混ぜない。
#[derive(Debug, Deserialize, Serialize)]
pub struct ApiFailure {
    pub code: String,
    #[serde(default = "empty_params")]
    pub params: Value,
}

fn empty_params() -> Value { json!({}) }

impl ApiFailure {
    pub async fn response(response: reqwest::Response) -> Self {
        let status = response.status();
        match response.json::<Self>().await {
            Ok(error) if error.params.is_object() && !error.code.is_empty() => error,
            result => {
                log::error!("Invalid core error response ({status}): {result:?}");
                Self::from("UNKNOWN_ERROR")
            }
        }
    }
}

impl From<String> for ApiFailure {
    fn from(value: String) -> Self {
        let is_code = !value.is_empty() && value.bytes().all(|c| c.is_ascii_uppercase() || c.is_ascii_digit() || c == b'_');
        let code = if is_code { value } else {
            log::error!("Native operation failed: {value}");
            "UNKNOWN_ERROR".into()
        };
        Self { code, params: empty_params() }
    }
}

impl From<&str> for ApiFailure {
    fn from(value: &str) -> Self { Self::from(value.to_owned()) }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn serialized_failure_preserves_parameters_without_diagnostic_text() {
        let error: ApiFailure = serde_json::from_value(json!({
            "code": "TRANSIT_STOP_INVALID_POSITION", "params": { "stop_number": 3 },
            "message": "private diagnostic"
        })).unwrap();
        assert_eq!(serde_json::to_value(error).unwrap(), json!({
            "code": "TRANSIT_STOP_INVALID_POSITION", "params": { "stop_number": 3 }
        }));
    }

    #[test]
    fn native_diagnostics_are_not_returned_as_display_text() {
        assert_eq!(ApiFailure::from("private diagnostic").code, "UNKNOWN_ERROR");
        assert_eq!(ApiFailure::from("BACKEND_DISCONNECTED").code, "BACKEND_DISCONNECTED");
    }
}
