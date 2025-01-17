﻿import {eventBus, ElsaPlugin} from "../services";
import {ActivityDesignDisplayContext, EventTypes} from "../models";
import {h} from "@stencil/core";
import {htmlEncode} from "../utils/utils";

export class CronPlugin implements ElsaPlugin {
  constructor() {
    eventBus.on(EventTypes.ActivityDesignDisplaying, this.onActivityDesignDisplaying);
  }

  onActivityDesignDisplaying(context: ActivityDesignDisplayContext) {
    const activityModel = context.activityModel;

    if (activityModel.type !== 'Cron')
      return;

    const props = activityModel.properties || [];
    const condition = props.find(x => x.name == 'CronExpression') || { name: 'CronExpression', expressions: {'Literal': ''}, syntax: 'Literal'};
    const expression = htmlEncode(condition.expressions[condition.syntax] || '');
    context.bodyDisplay = `<p>${expression}</p>`;
  }
}
