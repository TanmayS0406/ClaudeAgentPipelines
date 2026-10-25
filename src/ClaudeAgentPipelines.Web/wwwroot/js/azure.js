// Sign in to Azure: shows the Azure CLI sign-in on this computer, opens `az login` and polls until it finishes.
// On board settings it also fills the subscription list.
(function () {
    const widget = document.querySelector('[data-azure-status]');
    if (!widget) return;

    const text = widget.querySelector('[data-role="text"]');
    const loginButton = widget.querySelector('[data-role="login"]');
    const details = document.querySelector('[data-role="details"]');
    const refreshButton = document.querySelector('[data-role="refresh"]');
    const subscriptionSelect = document.getElementById('azure-subscription');
    const subscriptionName = document.getElementById('azure-subscription-name');
    const tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
    let subscriptionsLoaded = false;

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
        if (status.loggedIn && subscriptionSelect && !subscriptionsLoaded) loadSubscriptions();
    }

    async function loadSubscriptions() {
        subscriptionsLoaded = true;
        const subscriptions = await (await fetch(subscriptionSelect.dataset.subscriptionsUrl, { cache: 'no-store' })).json();
        const current = subscriptionSelect.value;
        subscriptionSelect.querySelectorAll('option:not([value=""])').forEach(o => o.remove());
        let found = false;
        subscriptions.forEach(s => {
            const option = new Option(s.name + ' (' + s.id + ')' + (s.isDefault ? ' - default' : ''), s.id);
            option.dataset.name = s.name;
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

    if (subscriptionSelect) {
        subscriptionSelect.addEventListener('change', () => {
            const option = subscriptionSelect.selectedOptions[0];
            subscriptionName.value = option && option.value ? (option.dataset.name || '') : '';
        });
    }

    async function poll() {
        while (true) {
            await new Promise(resolve => setTimeout(resolve, 4000));
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

    loginButton.addEventListener('click', async () => {
        loginButton.disabled = true;
        const response = await fetch(widget.dataset.startUrl, {
            method: 'POST',
            headers: { 'RequestVerificationToken': tokenInput ? tokenInput.value : '' }
        });
        if (!response.ok) {
            const body = await response.json().catch(() => ({}));
            text.textContent = body.error || 'Could not start the Azure sign-in.';
            loginButton.disabled = false;
            return;
        }
        text.textContent = 'An Azure sign-in window opened on this computer. Finish signing in there...';
        poll();
    });

    if (refreshButton) {
        refreshButton.addEventListener('click', async () => {
            text.textContent = 'Checking the Azure sign-in...';
            show(await getStatus(true));
        });
    }

    getStatus(false).then(status => {
        show(status);
        if (status.loginInProgress) poll();
    }).catch(() => text.textContent = 'Could not check the Azure sign-in.');
})();
