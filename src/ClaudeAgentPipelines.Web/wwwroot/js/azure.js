// Sign in to Azure: shows the Azure CLI sign-in on this computer, opens `az login` and polls until it finishes.
// It also loads every subscription the account can see: as a table on the Azure page (choose the CLI's default) and
// as the subscription list on a board's settings.
(function () {
    const widget = document.querySelector('[data-azure-status]');
    if (!widget) return;

    const text = widget.querySelector('[data-role="text"]');
    const loginButton = widget.querySelector('[data-role="login"]');
    const details = document.querySelector('[data-role="details"]');
    const refreshButton = document.querySelector('[data-role="refresh"]');
    const subscriptionSelect = document.getElementById('azure-subscription');
    const subscriptionName = document.getElementById('azure-subscription-name');
    const subscriptionCard = document.querySelector('[data-role="subscriptions"]');
    const subscriptionRows = document.querySelector('[data-role="subscription-rows"]');
    const subscriptionError = document.querySelector('[data-role="subscription-error"]');
    const tenantInput = document.querySelector('[data-role="tenant"]');
    const tenantButton = document.querySelector('[data-role="tenant-login"]');
    const tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
    const subscriptionsUrl = subscriptionSelect ? subscriptionSelect.dataset.subscriptionsUrl
        : subscriptionCard ? subscriptionCard.dataset.subscriptionsUrl : null;
    let subscriptionsLoaded = false;

    const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
    const token = () => ({ 'RequestVerificationToken': tokenInput ? tokenInput.value : '' });

    async function getStatus(refresh) {
        const response = await fetch(widget.dataset.statusUrl + (refresh ? '?refresh=true' : ''), { cache: 'no-store' });
        return response.json();
    }

    function show(status) {
        text.className = status.loggedIn ? '' : 'muted';
        if (!status.cliFound) {
            text.textContent = 'The Azure CLI is not installed on this computer. Install it from https://aka.ms/installazurecliwindows, then refresh.';
        } else if (status.loggedIn) {
            text.textContent = '✔ Signed in to Azure as ' + (status.user || 'unknown') +
                (status.subscriptionName ? ' - default subscription ' + status.subscriptionName : '');
        } else if (status.loginInProgress) {
            text.textContent = 'Finish signing in in the Azure sign-in window...';
        } else {
            text.textContent = 'Not signed in to Azure.' + (status.detail && status.detail !== 'Not signed in to Azure.' ? ' ' + status.detail : '');
        }
        loginButton.hidden = !status.cliFound || status.loggedIn;
        loginButton.disabled = !!status.loginInProgress;

        if (details) {
            details.hidden = !status.loggedIn;
            details.querySelectorAll('[data-field]').forEach(field => field.textContent = status[field.dataset.field] || '-');
        }
        if (subscriptionCard) subscriptionCard.hidden = !status.loggedIn;
        if (!status.loggedIn) subscriptionsLoaded = false;
        if (status.loggedIn && subscriptionsUrl && !subscriptionsLoaded) loadSubscriptions();
    }

    // ---- subscriptions

    async function loadSubscriptions() {
        subscriptionsLoaded = true;
        if (subscriptionError) subscriptionError.hidden = true;
        let subscriptions;
        try {
            subscriptions = await (await fetch(subscriptionsUrl, { cache: 'no-store' })).json();
        } catch {
            subscriptionsLoaded = false;
            if (subscriptionRows) fillRows([], 'Could not load the subscriptions. Click Refresh to try again.');
            return;
        }
        if (subscriptionSelect) fillSelect(subscriptions);
        if (subscriptionRows) fillRows(subscriptions);
    }

    // Board settings: the subscription to deploy to. Only enabled subscriptions can be chosen.
    function fillSelect(subscriptions) {
        const current = subscriptionSelect.value;
        subscriptionSelect.querySelectorAll('option:not([value=""])').forEach(o => o.remove());
        let found = false;
        subscriptions.forEach(s => {
            const label = s.name + ' (' + s.id + ')' + (s.isDefault ? ' - default' : '') + (s.isEnabled ? '' : ' - ' + s.state + ', cannot be used');
            const option = new Option(label, s.id);
            option.dataset.name = s.name;
            option.disabled = !s.isEnabled && s.id !== current;
            if (s.id === current) { option.selected = true; found = true; }
            subscriptionSelect.add(option);
        });
        if (current && !found) {
            // Keep a saved subscription this account can't see, so saving the form doesn't clear it.
            const option = new Option((subscriptionName.value || current) + ' (not available to this account)', current);
            option.dataset.name = subscriptionName.value;
            option.selected = true;
            subscriptionSelect.add(option);
        }
    }

    // Azure page: every subscription, with a button to make one the Azure CLI's default.
    function fillRows(subscriptions, emptyText) {
        subscriptionRows.replaceChildren();
        if (subscriptions.length === 0) {
            const row = subscriptionRows.insertRow();
            const cell = row.insertCell();
            cell.colSpan = 5;
            cell.className = 'muted';
            cell.textContent = emptyText || 'This account has no subscriptions the Azure CLI knows about. Sign in to the tenant that has them (below).';
            return;
        }
        subscriptions.forEach(s => {
            const row = subscriptionRows.insertRow();
            row.insertCell().textContent = s.name;
            const id = row.insertCell();
            id.textContent = s.id;
            id.className = 'mono';
            const tenant = row.insertCell();
            tenant.textContent = s.tenantId;
            tenant.className = 'mono';

            const state = row.insertCell();
            const badge = document.createElement('span');
            badge.className = 'badge';
            badge.textContent = s.state;
            state.append(badge);

            const action = row.insertCell();
            action.className = 'right';
            if (s.isDefault) {
                const current = document.createElement('span');
                current.className = 'status status-completed';
                current.textContent = 'default';
                action.append(current);
            } else if (s.isEnabled) {
                const button = document.createElement('button');
                button.type = 'button';
                button.className = 'btn btn-secondary';
                button.textContent = 'Use as default';
                button.addEventListener('click', () => setDefault(s, button));
                action.append(button);
            }
        });
    }

    async function setDefault(subscription, button) {
        subscriptionError.hidden = true;
        subscriptionRows.querySelectorAll('button').forEach(b => b.disabled = true);
        button.textContent = 'Switching...';
        try {
            const response = await fetch(subscriptionCard.dataset.setUrl, {
                method: 'POST',
                headers: { ...token(), 'Content-Type': 'application/x-www-form-urlencoded' },
                body: 'id=' + encodeURIComponent(subscription.id)
            });
            if (!response.ok) {
                const body = await response.json().catch(() => ({}));
                throw new Error(body.error || 'The Azure CLI could not switch to that subscription.');
            }
            subscriptionsLoaded = false;
            show(await getStatus(true));
        } catch (e) {
            subscriptionError.textContent = e.message;
            subscriptionError.hidden = false;
            subscriptionsLoaded = false;
            loadSubscriptions();
        }
    }

    if (subscriptionSelect) {
        subscriptionSelect.addEventListener('change', () => {
            const option = subscriptionSelect.selectedOptions[0];
            subscriptionName.value = option && option.value ? (option.dataset.name || '') : '';
        });
    }

    // ---- signing in

    async function poll() {
        while (true) {
            await sleep(4000);
            let status;
            try {
                status = await getStatus(true);
            } catch {
                continue;
            }
            show(status);
            if (status.loggedIn || !status.loginInProgress) return;
        }
    }

    // Signing in to another tenant while already signed in: wait for the sign-in window to close, then reload the list.
    async function pollTenantLogin() {
        while (true) {
            await sleep(4000);
            let status;
            try {
                status = await getStatus(true);
            } catch {
                continue;
            }
            if (!status.loginInProgress) {
                subscriptionsLoaded = false;
                show(status);
                return;
            }
        }
    }

    async function startLogin(tenant) {
        const response = await fetch(widget.dataset.startUrl, {
            method: 'POST',
            headers: { ...token(), 'Content-Type': 'application/x-www-form-urlencoded' },
            body: tenant ? 'tenant=' + encodeURIComponent(tenant) : ''
        });
        if (!response.ok) {
            const body = await response.json().catch(() => ({}));
            throw new Error(body.error || 'Could not start the Azure sign-in.');
        }
    }

    loginButton.addEventListener('click', async () => {
        loginButton.disabled = true;
        try {
            await startLogin();
        } catch (e) {
            text.textContent = e.message;
            loginButton.disabled = false;
            return;
        }
        text.textContent = 'An Azure sign-in window opened on this computer. Finish signing in there...';
        poll();
    });

    if (tenantButton) {
        tenantButton.addEventListener('click', async () => {
            subscriptionError.hidden = true;
            tenantButton.disabled = true;
            try {
                await startLogin(tenantInput.value.trim());
                text.textContent = 'An Azure sign-in window opened on this computer. Finish signing in to the tenant there...';
                pollTenantLogin();
            } catch (e) {
                subscriptionError.textContent = e.message;
                subscriptionError.hidden = false;
            } finally {
                tenantButton.disabled = false;
            }
        });
    }

    if (refreshButton) {
        refreshButton.addEventListener('click', async () => {
            text.textContent = 'Checking the Azure sign-in...';
            subscriptionsLoaded = false;
            show(await getStatus(true));
        });
    }

    getStatus(false).then(status => {
        show(status);
        if (status.loginInProgress) poll();
    }).catch(() => text.textContent = 'Could not check the Azure sign-in.');
})();
