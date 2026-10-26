// Purchase page: shows the total for the number of days entered (days x users x daily rate).
(function () {
    const form = document.getElementById('purchase-form');
    if (!form) return;

    const rate = parseFloat(form.dataset.rate);
    const users = parseInt(form.dataset.users, 10);
    const days = document.getElementById('days');
    const money = new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR' });

    function update() {
        const count = Math.min(365, Math.max(0, parseInt(days.value, 10) || 0));
        const total = money.format(count * users * rate);
        document.getElementById('summary-days').textContent = count;
        document.getElementById('total').textContent = total;
        document.getElementById('total-button').textContent = total;
    }

    days.addEventListener('input', update);
    form.querySelectorAll('[data-days]').forEach(button => {
        button.addEventListener('click', () => {
            days.value = button.dataset.days;
            update();
        });
    });
    update();
})();
