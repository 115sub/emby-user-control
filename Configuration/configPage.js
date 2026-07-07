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
            var minutesText = parseInt(limit.LimitMinutes, 10) > 0 ? escapeHtml(limit.LimitMinutes) + ' 分钟' : '不限制';
            var rangeText = getAllowedRangeText(limit);
            return '<tr class="eucRuleRow">'
                + '<td class="eucUserCell">' + escapeHtml(limit.Username) + '</td>'
                + '<td>' + minutesText + '</td>'
                + '<td>' + rangeText + '</td>'
                + '<td class="eucActionCell">'
                + '<button type="button" is="emby-button" class="btnDeleteLimit eucDeleteButton" data-index="' + index + '">删除</button>'
                + '</td>'
                + '</tr>';
        }).join('') || '<tr><td colspan="4" class="eucEmptyCell">暂无受限用户。请选择用户并设置时长或允许播放时间段，然后点击“加入列表”。</td></tr>';
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

    function isValidTime(value) {
        if (!value) {
            return true;
        }

        return /^([01]\d|2[0-3]):[0-5]\d$/.test(value);
    }

    function getAllowedRangeText(limit) {
        var start = limit.AllowedStartTime || '';
        var end = limit.AllowedEndTime || '';

        if ((!start || !end) && limit.CutoffTime) {
            start = '00:00';
            end = limit.CutoffTime;
        }

        return start && end ? escapeHtml(start + '-' + end) : '不限制';
    }

    function formatUserLabel(user) {
        var badges = [];
        if (user.IsDisabled) {
            badges.push('已禁用');
        }
        if (user.IsHidden) {
            badges.push('隐藏');
        }
        if (user.EnableMediaPlayback === false) {
            badges.push('播放关闭');
        }
        if (user.EnableRemoteAccess === false) {
            badges.push('远程关闭');
        }

        return user.Name + (badges.length ? '（' + badges.join(' / ') + '）' : '');
    }

    function loadUsers(view) {
        return ApiClient.getJSON(ApiClient.getUrl('EmbyUserControl/Users')).then(function (result) {
            var users = result && result.Items ? result.Items : [];
            var select = view.querySelector('#selectUser');
            if (!select) {
                return;
            }

            select.innerHTML = (users || []).map(function (user) {
                return user.Name ? '<option value="' + escapeHtml(user.Name) + '">' + escapeHtml(formatUserLabel(user)) + '</option>' : '';
            }).join('');

            if (!users.length) {
                select.innerHTML = '<option value="">未找到用户</option>';
            }
        });
    }

    function loadConfig(instance) {
        loading.show();

        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            var view = instance.view;
            var txt = view.querySelector('#txtTimeoutMessage');

            if (txt) {
                txt.value = config.TimeoutMessage || '当前不在允许播放时间段内，或今日播放时长已达上限，播放已被终止。';
            }

            instance.limits = (config.UserLimits || []).map(function (limit) {
                if ((!limit.AllowedStartTime || !limit.AllowedEndTime) && limit.CutoffTime) {
                    limit.AllowedStartTime = '00:00';
                    limit.AllowedEndTime = limit.CutoffTime;
                }
                return limit;
            });
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
        var startInput = view.querySelector('#inputAllowedStartTime');
        var endInput = view.querySelector('#inputAllowedEndTime');
        var user = select ? select.value : '';
        var rawMinutes = input ? String(input.value || '').trim() : '';
        var minutes = rawMinutes ? parseInt(rawMinutes, 10) : 0;
        var allowedStartTime = startInput ? String(startInput.value || '').trim() : '';
        var allowedEndTime = endInput ? String(endInput.value || '').trim() : '';
        var hasAllowedRange = allowedStartTime || allowedEndTime;

        if (!user || isNaN(minutes) || minutes < 0 || (!minutes && !hasAllowedRange)) {
            setStatus(view, '请选择用户，并至少设置一个大于 0 的分钟数或允许播放时间段。', 'isWarning');
            return;
        }
        if ((allowedStartTime && !allowedEndTime) || (!allowedStartTime && allowedEndTime)) {
            setStatus(view, '允许播放时间段需要同时填写开始和结束时间。', 'isWarning');
            return;
        }
        if (!isValidTime(allowedStartTime) || !isValidTime(allowedEndTime)) {
            setStatus(view, '时间格式应为 HH:mm，例如 19:30。', 'isWarning');
            return;
        }
        if (allowedStartTime && allowedStartTime === allowedEndTime) {
            setStatus(view, '允许播放时间段的开始和结束时间不能相同。', 'isWarning');
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
            existing.AllowedStartTime = allowedStartTime;
            existing.AllowedEndTime = allowedEndTime;
            existing.CutoffTime = '';
            render(view, instance.limits);
            setStatus(view, '已更新 ' + user + ' 的限制，点击“保存配置”后生效。', 'isOk');
            return;
        }

        instance.limits.push({
            Username: user,
            LimitMinutes: minutes,
            AllowedStartTime: allowedStartTime,
            AllowedEndTime: allowedEndTime,
            CutoffTime: ''
        });

        render(view, instance.limits);
        setStatus(view, '已把 ' + user + ' 加入列表，点击“保存配置”后写入后台。', 'isOk');
    }

    function isKnownDraft(instance) {
        var view = instance.view;
        var select = view.querySelector('#selectUser');
        var input = view.querySelector('#inputLimitMinutes');
        var startInput = view.querySelector('#inputAllowedStartTime');
        var endInput = view.querySelector('#inputAllowedEndTime');
        var user = select ? select.value : '';
        var rawMinutes = input ? String(input.value || '').trim() : '';
        var minutes = rawMinutes ? parseInt(rawMinutes, 10) : 0;
        var allowedStartTime = startInput ? String(startInput.value || '').trim() : '';
        var allowedEndTime = endInput ? String(endInput.value || '').trim() : '';
        var hasAllowedRange = allowedStartTime || allowedEndTime;

        if (!user || isNaN(minutes) || minutes < 0 || (!minutes && !hasAllowedRange)) {
            return false;
        }
        if ((allowedStartTime && !allowedEndTime) || (!allowedStartTime && allowedEndTime)) {
            return false;
        }
        if (!isValidTime(allowedStartTime) || !isValidTime(allowedEndTime)) {
            return false;
        }
        if (allowedStartTime && allowedStartTime === allowedEndTime) {
            return false;
        }

        return instance.limits.some(function (limit) {
            return String(limit.Username || '').toLowerCase() === user.toLowerCase();
        });
    }

    function onSubmit(instance, e) {
        e.preventDefault();

        if (instance.limits.length && !isKnownDraft(instance)) {
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
            setStatus(instance.view, instance.limits.length ? '配置已保存。' : '已保存空限制列表，后台将恢复已移除用户的权限。', 'isOk');
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
