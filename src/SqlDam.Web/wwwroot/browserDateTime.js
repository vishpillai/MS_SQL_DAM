window.sqlDamBrowserDateTime = {
    toUtcMilliseconds: (localDateTime) => {
        const parsed = new Date(localDateTime);
        return Number.isFinite(parsed.getTime()) ? parsed.getTime() : null;
    }
};