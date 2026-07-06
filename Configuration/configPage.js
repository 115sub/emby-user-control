define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-select'], function (BaseView, loading) {
    'use strict';

    var pluginId = 'A8B8808D-79CD-49D6-B06A-EC0542385C66';

    function escapeHtml(value) {
        return String(value || '').replace(/[&<>"']/g, function (ch) {
            return {
                '&': '&amp;',
                '<': '&lt;',
                '>': '&gt;',
                '"': '&quot;',
                "'": '&#39;'
            }[ch];
        });
    }

    function render(view, limits) {
        var tbody = view.querySelector('#tbodyUserLimits');
        if (!tbody) {
            return;
        }

        tbody.innerHTML = limits.map(function (limit, index) {
            return '<tr style="border-bottom:1px solid #edf1f5;">'
                + '<td style="padding:14px 10px;font-weight:700;color:#1f2933;">' + escapeHtml(limit.Username) + '</td>'
                + '<td style="padding:14px 10px;color:#3b4a5a;">' + escapeHtml(limit.LimitMinutes) + ' 分钟</td>'
                + '<td style="padding:14px 10px;text-align:right;">'
                + '<button type="button" class="raised btnDeleteLimit" data-index="' + index + '" style="padding:0.35em 0.9em;background:#d32f2f;color:#fff;border-radius:6px;border:none;cursor:pointer;">删除</button>'
                + '</td>'
                + '</tr>';
        }).join('') || '<tr><td colspan="3" style="padding:1.5em 0;text-align:center;color:#7b8794;">暂无受限用户。请选择用户和分钟数，然后点击“加入列表”。</td></tr>';
    }

    function setStatus(view, message, kind) {
        var status = view.querySelector('#configStatus');
        if (!status) {
            return;
        }

        status.classList.remove('isWarning');
        status.classList.remove('isOk');
        if (kind) {
            status.classList.add(kind);
        }
        status.textContent = message || '';
    }

    function loadUsers(view) {
        return ApiClient.getUsers().then(function (users) {
            var select = view.querySelector('#selectUser');
            if (!select) {
                return;
            }

            select.innerHTML = (users || []).map(function (user) {
                return user.Name ? '<option value="' + escapeHtml(user.Name) + '">' + escapeHtml(user.Name) + '</option>' : '';
            }).join('');
        });
    }

    function loadConfig(instance) {
        loading.show();

        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            var view = instance.view;
            var txt = view.querySelector('#txtTimeoutMessage');

            if (txt) {
                txt.value = config.TimeoutMessage || '您今日的播放时长已达上限';
            }

            instance.limits = config.UserLimits || [];
            render(view, instance.limits);
            setStatus(view, instance.limits.length ? '已载入 ' + instance.limits.length + ' 条限制规则。' : '当前还没有受限用户。', instance.limits.length ? 'isOk' : '');

            return loadUsers(view);
        }).then(function () {
            loading.hide();
        });
    }

    function onDeleteLimit(instance, e) {
        var button = e.target.closest ? e.target.closest('.btnDeleteLimit') : null;
        if (!button) {
            return;
        }

        var index = parseInt(button.getAttribute('data-index'), 10);
        if (!isNaN(index)) {
            var removed = instance.limits[index];
            instance.limits.splice(index, 1);
            render(instance.view, instance.limits);
            setStatus(instance.view, removed ? '已从列表移除 ' + removed.Username + '，点击“保存配置”后生效。' : '已移除规则，点击“保存配置”后生效。', 'isWarning');
        }
    }

    function onAddLimit(instance) {
        var view = instance.view;
        var select = view.querySelector('#selectUser');
        var input = view.querySelector('#inputLimitMinutes');
        var user = select ? select.value : '';
        var minutes = input ? parseInt(input.value, 10) : 0;

        if (!user || isNaN(minutes) || minutes <= 0) {
            setStatus(view, '请选择用户，并填写大于 0 的分钟数。', 'isWarning');
            return;
        }

        var existing = null;
        instance.limits.some(function (limit) {
            if (String(limit.Username || '').toLowerCase() === user.toLowerCase()) {
                existing = limit;
                return true;
            }
            return false;
        });

        if (existing) {
            existing.LimitMinutes = minutes;
            render(view, instance.limits);
            setStatus(view, '已更新 ' + user + ' 的限制为 ' + minutes + ' 分钟，点击“保存配置”后生效。', 'isOk');
            return;
        }

        instance.limits.push({
            Username: user,
            LimitMinutes: minutes
        });

        render(view, instance.limits);
        setStatus(view, '已把 ' + user + ' 加入列表，点击“保存配置”后写入后台。', 'isOk');
    }

    function isKnownDraft(instance) {
        var view = instance.view;
        var select = view.querySelector('#selectUser');
        var input = view.querySelector('#inputLimitMinutes');
        var user = select ? select.value : '';
        var minutes = input ? parseInt(input.value, 10) : 0;

        if (!user || isNaN(minutes) || minutes <= 0) {
            return false;
        }

        return instance.limits.some(function (limit) {
            return String(limit.Username || '').toLowerCase() === user.toLowerCase();
        });
    }

    function onSubmit(instance, e) {
        e.preventDefault();

        if (!instance.limits.length) {
            setStatus(instance.view, '还没有可保存的受限用户。请先点击“加入列表”，再保存配置。', 'isWarning');
            return false;
        }

        if (!isKnownDraft(instance)) {
            setStatus(instance.view, '当前输入区的用户尚未加入列表；本次只保存上方列表中的规则。', 'isWarning');
        }

        console.log('EmbyUserControl: saving limits', instance.limits);
        loading.show();

        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            var txt = instance.view.querySelector('#txtTimeoutMessage');
            config.TimeoutMessage = txt ? txt.value : '';
            config.UserLimits = instance.limits;

            return ApiClient.updatePluginConfiguration(pluginId, config);
        }).then(function () {
            loading.hide();
            setStatus(instance.view, '配置已保存。', 'isOk');
            Dashboard.processPluginConfigurationUpdateResult();
        });

        return false;
    }

    function View(view, params) {
        BaseView.apply(this, arguments);

        this.limits = [];
        view.__embyUserControlInstance = this;

        view.querySelector('.embyUserControlConfigurationForm').addEventListener('submit', onSubmit.bind(null, this));
        view.querySelector('#btnAddLimit').addEventListener('click', onAddLimit.bind(null, this));
        view.querySelector('#tbodyUserLimits').addEventListener('click', onDeleteLimit.bind(null, this));
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        return loadConfig(this);
    };

    return View;
});
