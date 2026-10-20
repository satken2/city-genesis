/// Handle HTTP failures separately from compatibility errors. A successful check resets consecutive failures.
#[derive(Default)]
pub struct BackendHealth {
    pub connected: bool,
    failures: u32,
}

#[derive(Debug, PartialEq)]
pub enum HealthAction { Continue, ReturnToMenu, Exit }

impl BackendHealth {
    pub fn observe(&mut self, success: bool, development: bool) -> HealthAction {
        self.connected = success;
        self.failures = if success { 0 } else { self.failures.saturating_add(1) };
        if self.failures < 2 { HealthAction::Continue }
        else if !development { HealthAction::Exit }
        else if self.failures == 2 { HealthAction::ReturnToMenu }
        else { HealthAction::Continue }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn transient_failure_preserves_session_and_success_resets_count() {
        for development in [false, true] {
            let mut health = BackendHealth::default();
            for success in [true, false, true, false, true] {
                assert_eq!(health.observe(success, development), HealthAction::Continue);
                assert_eq!(health.connected, success);
            }
        }
    }
    #[test]
    fn product_exits_on_two_failures_including_startup() {
        let mut health = BackendHealth::default();
        assert_eq!(health.observe(false, false), HealthAction::Continue);
        assert_eq!(health.observe(false, false), HealthAction::Exit);
    }
    #[test]
    fn development_clears_once_and_can_reconnect_repeatedly() {
        let mut health = BackendHealth::default();
        for _ in 0..2 {
            assert_eq!(health.observe(false, true), HealthAction::Continue);
            assert_eq!(health.observe(false, true), HealthAction::ReturnToMenu);
            assert_eq!(health.observe(false, true), HealthAction::Continue);
            assert_eq!(health.observe(true, true), HealthAction::Continue);
        }
    }
}
